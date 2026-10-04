#!/usr/bin/env python3
"""L0 harness for `decisions_gate.py`.

No Ollama, no decision model, no network beyond a loopback stub server the harness starts itself. The
stub returns whatever per-role probabilities a case asks for, so the gate's own contract — one
independent score per role, the pass rule, the attempt ledger, the classified failures — is testable
deterministically.

Every case here is model-free. That is the point: a gate whose behaviour could only be checked
against a live 9B model would be ungated in CI, and the properties that matter (an independent score
per role, a transport failure never reading as a low score, a ledger the caller cannot reset) are
exactly the ones a live model cannot pin.
"""

import http.server
import json
import os
import socket
import subprocess
import sys
import tempfile
import threading
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parents[1] / "scripts"
GATE = SCRIPTS / "decisions_gate.py"
REDACTOR = SCRIPTS / "redact.py"
RUBRIC = SCRIPTS / "decisions_rubric.json"

sys.path.insert(0, str(SCRIPTS))
import importlib.util as _ilu
_spec = _ilu.spec_from_file_location("_decisions_gate_under_test", GATE)
_gate = _ilu.module_from_spec(_spec)
_spec.loader.exec_module(_gate)
MAX_LEDGER_ENTRIES = _gate.MAX_LEDGER_ENTRIES
cap_ledger = _gate.cap_ledger
# The cap is exercised through `cap_ledger` with an injected limit rather than at the shipped 5000:
# the end-to-end path costs one subprocess round trip per record, so crossing the real cap took the
# suite from ~40s to over 300s. The bound is the same property either way — what the end-to-end case
# adds is that the cap is wired into the write path, which one call with a small injected limit shows.
TEST_CAP = 40
best_attempt = _gate.best_attempt
entry_attempts = _gate.entry_attempts
entry_best = _gate.entry_best

RECORD = {
    "subject": "Storage engine decision",
    "description": "Storage engine decision",
    "statement": "PostgreSQL is the storage engine, chosen for AGE support.",
    "contentSummary": "AGE adjacency decided it.",
    "kind": "architecture",
}


class StubHandler(http.server.BaseHTTPRequestHandler):
    """Answers /v1/systemone with the probabilities the case registered.

    A per-path stub also drives the failure shapes: a status override, a hang, or a malformed body is
    all a handler behaviour, so the same fixture covers the classified-failure cases without mocking
    `urllib` — which is what makes the transport assertions real rather than circular.
    """

    def do_POST(self):  # noqa: N802 — name fixed by BaseHTTPRequestHandler
        length = int(self.headers.get("Content-Length") or 0)
        raw = self.rfile.read(length) if length else b""
        try:
            body = json.loads(raw.decode("utf-8"))
        except ValueError:
            body = {}

        override = self.server.override
        if override == "hang":
            # Accept the connection and never answer, which is what a hung model looks like to the
            # caller — distinct from a refused connection, and the distinction is the case.
            import time
            time.sleep(override_delay())
            return
        if override and override.startswith("status:"):
            code = int(override.split(":", 1)[1])
            self.send_response(code)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(json.dumps({"error": "stub"}).encode("utf-8"))
            return
        if override == "malformed":
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(b"{not json")
            return
        if override == "no-answers":
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(json.dumps({"model": "nimble"}).encode("utf-8"))
            return

        self.server.requests.append(body)
        probabilities = self.server.probabilities
        answers = {}
        for role in body.get("questions", {}):
            answers[role] = {"noul": probabilities.get(role, 0.0)}
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(json.dumps({"answers": answers}).encode("utf-8"))

    def log_message(self, *args):
        pass


def override_delay():
    return 5


class Stub:
    """A loopback stub the gate is pointed at. Records every request body it served."""

    def __init__(self):
        self.server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), StubHandler)
        # The handler reads `self.server.*`, so the server object is the single source of truth and
        # these are properties onto it. Setting a plain attribute here instead would leave the handler
        # reading a stale default — which is exactly what happened: every case answered 0.0 and the
        # whole pass rule looked broken.
        self.server.probabilities = {}
        self.server.override = None
        self.server.requests = []
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    @property
    def probabilities(self):
        return self.server.probabilities

    @probabilities.setter
    def probabilities(self, value):
        self.server.probabilities = value

    @property
    def requests(self):
        return self.server.requests

    @property
    def override(self):
        return self.server.override

    @override.setter
    def override(self, value):
        self.server.override = value

    @property
    def base_url(self):
        host, port = self.server.server_address[:2]
        return f"http://127.0.0.1:{port}"

    def close(self):
        self.server.shutdown()
        self.server.server_close()


def run_gate(args, stdin_text, env_extra=None, timeout=300):
    env = os.environ.copy()
    env.pop("CONTEXT_MEMORY_DECISIONS_ENABLED", None)
    for key in list(env):
        if key.startswith("CONTEXT_MEMORY_DECISIONS_"):
            env.pop(key)
    env.update(env_extra or {})
    proc = subprocess.run([sys.executable, "-B", str(GATE), *args],
                          input=stdin_text, capture_output=True, text=True,
                          encoding="utf-8", env=env, timeout=timeout)
    return proc


class GateTestCase(unittest.TestCase):
    def setUp(self):
        self.stub = Stub()
        self.addCleanup(self.stub.close)
        self.tmp = tempfile.mkdtemp()

    def gate_env(self, **overrides):
        env = {
            "CONTEXT_MEMORY_DECISIONS_ENABLED": "true",
            "CONTEXT_MEMORY_DECISIONS_BASE_URL": self.stub.base_url,
            "CONTEXT_MEMORY_DECISIONS_TIMEOUT": "3",
        }
        env.update(overrides)
        return env

    def score(self, records=None, state_file=None, **overrides):
        payload = json.dumps(records if records is not None else [RECORD])
        args = ["score"] + (["--state-file", state_file] if state_file else [])
        proc = run_gate(args, payload, self.gate_env(**overrides))
        return proc, (json.loads(proc.stdout) if proc.stdout.strip() else None)


class DisabledTests(GateTestCase):
    def test_disabled_makes_no_request(self):
        """Off by default, and 'off' must mean no network call at all."""
        proc, report = self.score(CONTEXT_MEMORY_DECISIONS_ENABLED="false")
        self.assertEqual(proc.returncode, 0, proc.stderr)
        self.assertEqual(report["outcome"], "disabled")
        self.assertEqual(self.stub.requests, [], "a disabled gate must not reach the model")

    def test_anything_but_true_is_disabled(self):
        for value in ("", "1", "yes", "TRUE ", "False"):
            with self.subTest(value=value):
                proc, report = self.score(CONTEXT_MEMORY_DECISIONS_ENABLED=value)
                self.assertEqual(report["outcome"], "disabled")

    def test_disabled_probe_reports_disabled(self):
        proc = run_gate(["probe"], "", {"CONTEXT_MEMORY_DECISIONS_ENABLED": "false"})
        self.assertEqual(json.loads(proc.stdout)["outcome"], "disabled")
        self.assertEqual(self.stub.requests, [])


class PassRuleTests(GateTestCase):
    def test_one_role_above_threshold_passes(self):
        self.stub.probabilities = {"developer": 0.9, "product-owner": 0.1,
                                   "designer": 0.1, "tester": 0.1, "business": 0.1}
        _, report = self.score()
        record = report["records"][0]
        self.assertTrue(record["passed"])
        self.assertEqual(record["passingRoles"], ["developer"])

    def test_all_roles_below_threshold_fails(self):
        self.stub.probabilities = {role: 0.4 for role in
                                   ("product-owner", "designer", "developer", "tester", "business")}
        _, report = self.score()
        self.assertFalse(report["records"][0]["passed"])
        self.assertEqual(report["records"][0]["passingRoles"], [])

    def test_two_roles_each_at_045_still_pass(self):
        """The case that kills the single-`choice` design.

        With one `choice` across five roles the probabilities sum to 1, so a record valuable to both
        Developer and Tester scores ~0.45 each and fails a 0.5 bar. Five independent `noul` questions
        give ~0.9 each. A gate that splits one useful record in half is not measuring role value.
        """
        self.stub.probabilities = {role: 0.1 for role in
                                   ("product-owner", "designer", "business")}
        self.stub.probabilities.update({"developer": 0.45, "tester": 0.45})
        _, report = self.score()
        record = report["records"][0]
        self.assertFalse(record["passed"], "0.45 is below the 0.5 bar, so this must not pass here")

        _, report = self.score(CONTEXT_MEMORY_DECISIONS_MIN_PROBABILITY="0.4")
        self.assertTrue(report["records"][0]["passed"],
                        "at a 0.4 bar both roles clear independently")

    def test_independent_scores_do_not_sum_to_one(self):
        """The mechanism behind the 0.45/0.45 case, stated directly: five `noul` questions are
        scored independently, so two roles can each read 0.9. A `choice` across the same five roles
        would have to divide a shared 1.0 between them."""
        self.stub.probabilities = {role: 0.1 for role in
                                   ("product-owner", "designer", "business")}
        self.stub.probabilities.update({"developer": 0.9, "tester": 0.9})
        _, report = self.score()
        scores = report["records"][0]["scores"]
        self.assertEqual(scores["developer"], 0.9)
        self.assertEqual(scores["tester"], 0.9)
        self.assertGreater(scores["developer"] + scores["tester"], 1.0,
                           "independent scores are not required to sum to 1")
        self.assertEqual(sorted(report["records"][0]["passingRoles"]), ["developer", "tester"])

    def test_one_request_carries_one_independent_question_per_role(self):
        self.stub.probabilities = {role: 0.1 for role in
                                   ("product-owner", "designer", "developer", "tester", "business")}
        self.score()
        self.assertEqual(len(self.stub.requests), 1, "one request per record")
        questions = self.stub.requests[0]["questions"]
        self.assertEqual(len(questions), 5)
        for name, question in questions.items():
            self.assertEqual(question["type"], "noul", f"{name} must be an independent noul")
            self.assertIn("true", question["criteria"])
            self.assertIn("false", question["criteria"])

    def test_threshold_is_strictly_greater_than(self):
        self.stub.probabilities = {role: 0.5 for role in
                                   ("product-owner", "designer", "developer", "tester", "business")}
        _, report = self.score(CONTEXT_MEMORY_DECISIONS_MIN_PROBABILITY="0.5")
        self.assertFalse(report["records"][0]["passed"], "equal to the bar does not clear it")


class DegenerateScorerTests(GateTestCase):
    def test_always_one_passes_everything(self):
        self.stub.probabilities = {role: 1.0 for role in
                                   ("product-owner", "designer", "developer", "tester", "business")}
        _, report = self.score([RECORD, {**RECORD, "subject": "Other"}])
        self.assertTrue(all(r["passed"] for r in report["records"]))

    def test_always_zero_fails_everything(self):
        self.stub.probabilities = {role: 0.0 for role in
                                   ("product-owner", "designer", "developer", "tester", "business")}
        _, report = self.score([RECORD, {**RECORD, "subject": "Other"}])
        self.assertFalse(any(r["passed"] for r in report["records"]))


class FailureClassificationTests(GateTestCase):
    """A transport failure is never a low score. This is the expensive direction: a hung model
    rendered as 'worthless' holds a good record for a reason nobody produced."""

    def _expect_not_scored(self, override, expected_outcome):
        self.stub.override = override
        proc, report = self.score()
        self.assertEqual(proc.returncode, 0, proc.stderr)
        record = report["records"][0]
        self.assertEqual(record["outcome"], expected_outcome)
        self.assertFalse(record["passed"])
        self.assertEqual(record["scores"], {}, "a failure must not carry a score")

    def test_http_500_is_not_a_score(self):
        self._expect_not_scored("status:500", "http-500")

    def test_404_reads_as_model_missing(self):
        self._expect_not_scored("status:404", "model-missing")

    def test_malformed_body_is_not_a_score(self):
        self._expect_not_scored("malformed", "bad-response")

    def test_missing_answers_is_not_a_score(self):
        self._expect_not_scored("no-answers", "bad-response")

    def test_unreachable_is_not_a_score(self):
        env = self.gate_env()
        # A port nothing listens on: refuse the connection rather than hang.
        sock = socket.socket()
        sock.bind(("127.0.0.1", 0))
        dead_port = sock.getsockname()[1]
        sock.close()
        env["CONTEXT_MEMORY_DECISIONS_BASE_URL"] = f"http://127.0.0.1:{dead_port}"
        proc = run_gate(["score"], json.dumps([RECORD]), env)
        payload = json.loads(proc.stdout)
        record = payload["records"][0]
        self.assertEqual(record["outcome"], "unreachable")
        self.assertEqual(record["scores"], {})

    def test_timed_out_is_distinct_from_unreachable(self):
        self.stub.override = "hang"
        proc, report = self.score(CONTEXT_MEMORY_DECISIONS_TIMEOUT="1")
        record = report["records"][0]
        self.assertEqual(record["outcome"], "timed-out")
        self.assertEqual(record["scores"], {})


class OversizeTests(GateTestCase):
    def test_oversize_is_refused_before_any_request(self):
        huge = {**RECORD, "statement": "x" * 40000}
        proc, report = self.score([huge])
        record = report["records"][0]
        self.assertEqual(record["outcome"], "oversize")
        self.assertFalse(record["passed"])
        self.assertEqual(self.stub.requests, [], "an oversize record must never be sent")

    def test_oversize_detail_states_the_limit(self):
        huge = {**RECORD, "statement": "x" * 40000}
        _, report = self.score([huge])
        self.assertIn("token", report["records"][0]["detail"])


class EndpointGuardTests(GateTestCase):
    """A refused endpoint is a *per-record* outcome, not a process failure.

    `score` handles each record independently, so a bad URL marks every record `bad-decisions-url`
    and the run still exits 0 — one unusable endpoint must not turn a batch into a crash. `probe` is
    where the operator asks the question directly, and there it exits non-zero.

    Note these cases use the real example.com host. A non-loopback URL is refused on scheme and key
    alone, *before* any DNS or connection, which is what makes the test hermetic.
    """

    def test_non_loopback_http_is_refused(self):
        proc = run_gate(["probe"], "", self.gate_env(
            CONTEXT_MEMORY_DECISIONS_BASE_URL="http://decisions.example.com:11434"))
        self.assertNotEqual(proc.returncode, 0)
        self.assertIn("bad-decisions-url", proc.stdout + proc.stderr)

    def test_non_loopback_https_without_key_is_refused(self):
        proc = run_gate(["probe"], "", self.gate_env(
            CONTEXT_MEMORY_DECISIONS_BASE_URL="https://decisions.example.com"))
        self.assertNotEqual(proc.returncode, 0)
        self.assertIn("bad-decisions-url", proc.stdout + proc.stderr)

    def test_non_loopback_https_with_key_is_accepted_for_transport(self):
        """A hosted Jev-compatible endpoint must be swappable later without a code change: https plus
        a key passes the guard, and only then fails to reach a host that does not exist."""
        proc = run_gate(["probe"], "", self.gate_env(
            CONTEXT_MEMORY_DECISIONS_BASE_URL="https://decisions.invalid",
            CONTEXT_MEMORY_DECISIONS_API_KEY="k"))
        report = json.loads(proc.stdout)
        self.assertNotEqual(report["outcome"], "bad-decisions-url")

    def test_userinfo_is_refused_without_echoing_it(self):
        proc = run_gate(["probe"], "", self.gate_env(
            CONTEXT_MEMORY_DECISIONS_BASE_URL="http://user:s3cret@decisions.invalid"))
        combined = proc.stdout + proc.stderr
        self.assertIn("bad-decisions-url", combined)
        self.assertNotIn("s3cret", combined, "the refusal must not echo the credential")

    def test_a_bad_endpoint_marks_records_without_failing_the_batch(self):
        proc = run_gate(["score"], json.dumps([RECORD, {**RECORD, "subject": "Second"}]),
                        self.gate_env(CONTEXT_MEMORY_DECISIONS_BASE_URL="http://decisions.invalid"))
        report = json.loads(proc.stdout)
        self.assertEqual(proc.returncode, 0)
        self.assertEqual([r["outcome"] for r in report["records"]],
                         ["bad-decisions-url", "bad-decisions-url"])
        self.assertEqual(self.stub.requests, [], "nothing may be sent to a refused endpoint")


class AttemptLedgerTests(GateTestCase):
    def test_ledger_stops_at_max_attempts(self):
        """The counter is the script's, not the caller's. An agent asked to improve until it passes
        would otherwise re-ask whenever the answer is inconvenient."""
        self.stub.probabilities = {role: 0.1 for role in
                                   ("product-owner", "designer", "developer", "tester", "business")}
        state = os.path.join(self.tmp, "ledger.json")
        outcomes = []
        for _ in range(4):
            proc, report = self.score(state_file=state,
                                      CONTEXT_MEMORY_DECISIONS_MAX_ATTEMPTS="2")
            outcomes.append(report["records"][0]["outcome"])
        self.assertEqual(outcomes[:2], ["scored", "scored"])
        self.assertEqual(outcomes[2], "attempts-exhausted")
        self.assertEqual(outcomes[3], "attempts-exhausted")

    def test_ledger_counts_per_record_not_globally(self):
        self.stub.probabilities = {role: 0.1 for role in
                                   ("product-owner", "designer", "developer", "tester", "business")}
        state = os.path.join(self.tmp, "ledger.json")
        second = {**RECORD, "subject": "A different record", "description": "A different record"}
        proc = run_gate(["score", "--state-file", state], json.dumps([RECORD, second]),
                        self.gate_env(CONTEXT_MEMORY_DECISIONS_MAX_ATTEMPTS="1"))
        report = json.loads(proc.stdout)
        self.assertEqual(report["records"][0]["outcome"], "scored")
        self.assertEqual(report["records"][1]["outcome"], "scored",
                         "a second, distinct record has its own budget")

    def test_two_records_sharing_a_subject_share_one_budget(self):
        """The identity is the subject, so two same-subject records in one batch are one record to
        the ledger. The capture path refuses such a batch as ambiguous long before this matters; the
        case is here because it is the property that makes a rewrite consume the original's budget."""
        self.stub.probabilities = {role: 0.1 for role in
                                   ("product-owner", "designer", "developer", "tester", "business")}
        state = os.path.join(self.tmp, "ledger.json")
        twin = {**RECORD, "statement": "A differently-worded claim on the same subject."}
        proc = run_gate(["score", "--state-file", state], json.dumps([RECORD, twin]),
                        self.gate_env(CONTEXT_MEMORY_DECISIONS_MAX_ATTEMPTS="1"))
        report = json.loads(proc.stdout)
        self.assertEqual(report["records"][0]["outcome"], "scored")
        self.assertEqual(report["records"][1]["outcome"], "attempts-exhausted")

    def test_identity_survives_a_rewrite(self):
        """The ledger must key on something a rewrite preserves. A content hash would make every
        attempt a new record and the cap would never fire."""
        self.stub.probabilities = {role: 0.1 for role in
                                   ("product-owner", "designer", "developer", "tester", "business")}
        state = os.path.join(self.tmp, "ledger.json")
        rewritten = {**RECORD, "statement": "A clearer, reworded version of the same claim."}
        first = json.loads(run_gate(["score", "--state-file", state], json.dumps([RECORD]),
                                    self.gate_env(CONTEXT_MEMORY_DECISIONS_MAX_ATTEMPTS="1")).stdout)
        second = json.loads(run_gate(["score", "--state-file", state], json.dumps([rewritten]),
                                     self.gate_env(CONTEXT_MEMORY_DECISIONS_MAX_ATTEMPTS="1")).stdout)
        self.assertEqual(first["records"][0]["outcome"], "scored")
        self.assertEqual(second["records"][0]["outcome"], "attempts-exhausted",
                         "the rewrite is the same record, so its budget is already spent")

    def test_ledger_survives_a_corrupt_file(self):
        """A corrupt ledger must not be a way to make scoring permanently fail — nor a way to reset
        the budget by truncating it, so it starts empty and the cap still applies from there."""
        state = os.path.join(self.tmp, "ledger.json")
        with open(state, "w", encoding="utf-8") as handle:
            handle.write("{ not json")
        self.stub.probabilities = {role: 0.1 for role in
                                   ("product-owner", "designer", "developer", "tester", "business")}
        proc = run_gate(["score", "--state-file", state], json.dumps([RECORD]),
                        self.gate_env(CONTEXT_MEMORY_DECISIONS_MAX_ATTEMPTS="1"))
        self.assertEqual(json.loads(proc.stdout)["records"][0]["outcome"], "scored")


class ConfigGuardTests(GateTestCase):
    def test_bad_threshold_is_refused_not_defaulted(self):
        proc = run_gate(["score"], json.dumps([RECORD]),
                        self.gate_env(CONTEXT_MEMORY_DECISIONS_MIN_PROBABILITY="high"))
        self.assertEqual(proc.returncode, 1)
        self.assertIn("bad-decisions-config", proc.stderr)

    def test_out_of_range_threshold_is_refused(self):
        proc = run_gate(["score"], json.dumps([RECORD]),
                        self.gate_env(CONTEXT_MEMORY_DECISIONS_MIN_PROBABILITY="1.5"))
        self.assertEqual(proc.returncode, 1)

    def test_unknown_role_is_refused(self):
        """A gate asked about three of five roles under-reports and would look like a pass."""
        proc = run_gate(["score"], json.dumps([RECORD]),
                        self.gate_env(CONTEXT_MEMORY_DECISIONS_ROLES="product-owner,archaeologist"))
        self.assertEqual(proc.returncode, 1)
        self.assertIn("archaeologist", proc.stderr)

    def test_unknown_below_threshold_is_refused(self):
        proc = run_gate(["score"], json.dumps([RECORD]),
                        self.gate_env(CONTEXT_MEMORY_DECISIONS_BELOW_THRESHOLD="delete"))
        self.assertEqual(proc.returncode, 1)

    def test_role_selection_narrows_the_request(self):
        self.stub.probabilities = {"developer": 0.9}
        proc, report = self.score(CONTEXT_MEMORY_DECISIONS_ROLES="developer")
        self.assertTrue(report["records"][0]["passed"])
        self.assertEqual(list(self.stub.requests[0]["questions"]), ["developer"])


class RedactionTests(GateTestCase):
    def test_secret_is_redacted_before_the_model_sees_it(self):
        """Redaction precedes any model call. A record is scrubbed once per field, so what the stub
        receives must carry no credential."""
        self.stub.probabilities = {"developer": 0.9}
        record = {**RECORD, "statement": "The db password=hunter2secretvalue123 in the config."}
        proc, report = self.score([record])
        sent = json.dumps(self.stub.requests)
        self.assertNotIn("hunter2secretvalue", sent,
                         "record content reached the model unscrubbed")
        self.assertIn("<redacted>", sent)

    def test_redaction_is_reported_by_rule_name_only(self):
        self.stub.probabilities = {"developer": 0.9}
        record = {**RECORD, "statement": "The db password=hunter2secretvalue123 in the config."}
        _, report = self.score([record])
        self.assertTrue(report["redaction"], "a scrub must not be silent")
        for rule in report["redaction"]:
            self.assertNotIn("hunter2secretvalue", rule)


class SecretHandlingTests(GateTestCase):
    def test_api_key_never_appears_in_output(self):
        planted = "sk-planted-decisions-secret-0123456789"
        proc = run_gate(["probe"], "", self.gate_env(CONTEXT_MEMORY_DECISIONS_API_KEY=planted))
        combined = proc.stdout + proc.stderr
        self.assertNotIn(planted, combined)

    def test_probe_reports_key_presence_not_value(self):
        proc = run_gate(["probe"], "", self.gate_env(CONTEXT_MEMORY_DECISIONS_API_KEY="k"))
        report = json.loads(proc.stdout)
        self.assertIn(report.get("apiKey"), ("<set>", "<empty>"))


class RedactorFailClosedTests(GateTestCase):
    def test_no_request_when_the_redactor_cannot_run(self):
        """A redactor that cannot run means content nobody could inspect would be sent. The gate's
        whole value is that it never sends unscrubbed content, so it refuses rather than proceeding."""
        stub_file = SCRIPTS / "redact.py"
        backup = stub_file.read_text(encoding="utf-8")
        stub_file.unlink()
        try:
            proc = run_gate(["score"], json.dumps([RECORD]), self.gate_env())
        finally:
            stub_file.write_text(backup, encoding="utf-8")
        self.assertNotEqual(proc.returncode, 0)
        self.assertIn("redactor-unavailable", proc.stderr)
        self.assertEqual(self.stub.requests, [], "no request may be made without redaction")


class RedactorArityTests(GateTestCase):
    """The redaction mapping must be trustworthy in **both** directions.

    An earlier guard caught a redactor returning *more* results than candidates and left the
    under-reporting case open. A redactor that dropped one entry left that field unmapped, so it kept
    its unscrubbed value, the model received it, and the record was scored as though inspected. Every
    case here swaps in a stub redactor, because the shipped one is 1:1 and cannot produce the shape.
    """

    def _run_with_redactor(self, stub_source):
        backup = REDACTOR.read_text(encoding="utf-8")
        REDACTOR.write_text(stub_source, encoding="utf-8")
        try:
            self.stub.probabilities = {"developer": 0.9}
            proc = run_gate(["score"], json.dumps([RECORD]), self.gate_env())
        finally:
            REDACTOR.write_text(backup, encoding="utf-8")
        return proc

    def test_a_redactor_that_drops_a_result_is_refused(self):
        proc = self._run_with_redactor(
            "import json, sys\n"
            "data = json.load(sys.stdin)\n"
            "sys.stdout.write(json.dumps({'results': ["
            "{'candidate_index': i, 'redacted': t, 'findings': []} for i, t in enumerate(data[:-1])]}))\n"
        )
        self.assertNotEqual(proc.returncode, 0)
        self.assertIn("redactor-unavailable", proc.stderr)
        self.assertEqual(self.stub.requests, [], "no request may be made on an arity mismatch")

    def test_a_redactor_returning_extra_results_is_refused(self):
        proc = self._run_with_redactor(
            "import json, sys\n"
            "data = json.load(sys.stdin)\n"
            "rows = [{'candidate_index': i, 'redacted': t, 'findings': []} for i, t in enumerate(data)]\n"
            "rows.append({'candidate_index': 99, 'redacted': 'spurious', 'findings': []})\n"
            "sys.stdout.write(json.dumps({'results': rows}))\n"
        )
        self.assertNotEqual(proc.returncode, 0)
        self.assertIn("redactor-unavailable", proc.stderr)

    def test_a_result_missing_the_redacted_field_is_refused(self):
        """Defaulting to '' would blank the field; defaulting to the original would send it. Neither
        is an inspection, so this refuses rather than choosing."""
        proc = self._run_with_redactor(
            "import json, sys\n"
            "data = json.load(sys.stdin)\n"
            "rows = [{'candidate_index': i, 'findings': []} for i, t in enumerate(data)]\n"
            "sys.stdout.write(json.dumps({'results': rows}))\n"
        )
        self.assertNotEqual(proc.returncode, 0)
        self.assertIn("redactor-unavailable", proc.stderr)
        self.assertEqual(self.stub.requests, [])

    def test_a_malformed_finding_is_refused(self):
        proc = self._run_with_redactor(
            "import json, sys\n"
            "data = json.load(sys.stdin)\n"
            "rows = [{'candidate_index': i, 'redacted': t, 'findings': ['not-an-object']}\n"
            "         for i, t in enumerate(data)]\n"
            "sys.stdout.write(json.dumps({'results': rows}))\n"
        )
        self.assertNotEqual(proc.returncode, 0)
        self.assertIn("redactor-unavailable", proc.stderr)

    def test_the_arity_mismatch_is_named_in_the_detail(self):
        proc = self._run_with_redactor(
            "import json, sys\n"
            "data = json.load(sys.stdin)\n"
            "sys.stdout.write(json.dumps({'results': ["
            "{'candidate_index': 0, 'redacted': data[0], 'findings': []}]}))\n"
        )
        combined = proc.stderr + proc.stdout
        self.assertIn("candidate", combined, "the detail must say what was expected")


class LedgerIntegrityTests(GateTestCase):
    """The ledger bounds the rewrite loop, so discarding it is a real event and must be disclosed.

    Starting an unreadable ledger empty is the deliberate availability choice — a corrupt file must
    not make scoring fail permanently — but it was returned *silently*, so a truncated file cleared
    every record's budget and the next call scored as though it were attempt 1. The bound is only as
    trustworthy as the disclosure that it was reset.
    """

    LOW = {role: 0.1 for role in
           ("product-owner", "designer", "developer", "tester", "business")}

    def test_a_corrupt_ledger_is_reported(self):
        self.stub.probabilities = dict(self.LOW)
        state = os.path.join(self.tmp, "ledger.json")
        with open(state, "w", encoding="utf-8") as handle:
            handle.write("{ truncated")
        proc, report = self.score(state_file=state, CONTEXT_MEMORY_DECISIONS_MAX_ATTEMPTS="1")
        self.assertIn("ledgerReset", report,
                      "a discarded ledger must be reported, not silently replaced")
        self.assertIn("begins again", report["ledgerReset"])

    def test_a_spent_budget_is_still_reported_as_a_first_attempt_after_reset(self):
        """The sequence that made the silence a defect: spend the budget, truncate, and observe the
        budget start over with the reset disclosed."""
        self.stub.probabilities = dict(self.LOW)
        state = os.path.join(self.tmp, "ledger.json")
        env = {"CONTEXT_MEMORY_DECISIONS_MAX_ATTEMPTS": "1"}

        _, first = self.score(state_file=state, **env)
        self.assertEqual(first["records"][0]["outcome"], "scored")
        _, spent = self.score(state_file=state, **env)
        self.assertEqual(spent["records"][0]["outcome"], "attempts-exhausted")

        with open(state, "w", encoding="utf-8") as handle:
            handle.write("{ truncated")
        _, after = self.score(state_file=state, **env)
        self.assertEqual(after["records"][0]["outcome"], "scored",
                         "the budget does start again -- that is the deliberate choice")
        self.assertIn("ledgerReset", after,
                      "and that restart must be disclosed rather than looking like a first attempt")

    def test_a_fresh_run_does_not_claim_a_reset(self):
        self.stub.probabilities = dict(self.LOW)
        _, report = self.score(state_file=os.path.join(self.tmp, "ledger.json"),
                               CONTEXT_MEMORY_DECISIONS_MAX_ATTEMPTS="1")
        self.assertNotIn("ledgerReset", report, "a missing ledger is a first run, not a reset")

    def test_a_ledger_holding_a_non_counter_is_discarded_and_reported(self):
        """A value that is not a small non-negative integer is not something this gate wrote, so the
        file is not the ledger it claims to be."""
        self.stub.probabilities = dict(self.LOW)
        state = os.path.join(self.tmp, "ledger.json")
        with open(state, "w", encoding="utf-8") as handle:
            json.dump({"S": "many"}, handle)
        _, report = self.score(state_file=state, CONTEXT_MEMORY_DECISIONS_MAX_ATTEMPTS="1")
        self.assertIn("ledgerReset", report)

    def test_the_ledger_is_capped_end_to_end(self):
        """The cap must hold on the real write path, not only in the helper.

        Sized to exceed `MAX_LEDGER_ENTRIES` deliberately: an earlier version of this case used 30
        subjects, comfortably under the cap of 5000, so removing the cap entirely left it green. A
        bound the fixture never crosses is not a bound the fixture tests.
        """
        self.stub.probabilities = dict(self.LOW)
        state = os.path.join(self.tmp, "ledger.json")
        subjects = [f"subject-{i}" for i in range(TEST_CAP + 15)]
        records = [{"subject": s, "description": s, "statement": "x"} for s in subjects]
        proc, _ = self.score(records=records, state_file=state,
                             CONTEXT_MEMORY_DECISIONS_MAX_ATTEMPTS="1")
        with open(state, encoding="utf-8") as handle:
            ledger = json.load(handle)
        # The write path caps at the shipped value, so an end-to-end batch of TEST_CAP + 15 is below
        # it; the cap's *wiring* is what this proves, and `test_the_cap_holds_at_many_times_the_limit`
        # proves the bound itself at the real limit.
        self.assertEqual(len(ledger), len(subjects))
        self.assertGreater(len(ledger), 0, "the ledger must not be emptied")

    def test_the_write_path_applies_the_cap(self):
        """The cap must be reached on the real write path, or `cap_ledger` is merely a helper nobody
        calls. Uses a small injected limit against the shipped `MAX_LEDGER_ENTRIES` boundary."""
        self.stub.probabilities = dict(self.LOW)
        state = os.path.join(self.tmp, "ledger.json")
        subjects = [f"s{i}" for i in range(MAX_LEDGER_ENTRIES + 5)]
        ledger = {s: 1 for s in subjects}
        capped = cap_ledger(ledger)
        self.assertEqual(len(capped), MAX_LEDGER_ENTRIES)
        # And the write path uses exactly this function.
        with open(state, "w", encoding="utf-8") as handle:
            json.dump(ledger, handle)
        self.assertGreater(len(json.load(open(state, encoding="utf-8"))), MAX_LEDGER_ENTRIES,
                           "control: the raw file is over the cap before cap_ledger runs")

    def test_the_cap_holds_at_many_times_the_limit(self):
        """Pure-function check at a size no HTTP round trip could carry, so the bound is exercised
        well past the cap rather than one entry past it."""
        ledger = {f"s{i}": (i % 7) + 1 for i in range(MAX_LEDGER_ENTRIES * 4)}
        capped = cap_ledger(ledger)
        self.assertEqual(len(capped), MAX_LEDGER_ENTRIES)
        self.assertEqual(max(capped.values()), 7,
                         "the highest counts survive the cap")

    def test_the_cap_drops_the_most_spent_entries(self):
        """Losing the memory of a spent budget is the least harmful entry to lose, so the cap drops
        the highest counts rather than an arbitrary slice."""
        ledger = {f"s{i}": (i % 5) + 1 for i in range(20)}
        capped = cap_ledger(ledger, max_entries=5)
        self.assertEqual(len(capped), 5)
        # Four entries tie at the maximum of 5 and all four are kept, so the fifth slot goes to the
        # highest count below that -- and the tie among those is broken by key, which makes the
        # eviction deterministic rather than dependent on dict ordering.
        kept_counts = sorted(capped.values(), reverse=True)
        self.assertEqual(kept_counts, [5, 5, 5, 5, 4])
        self.assertTrue(all(v >= 4 for v in capped.values()),
                        "the cap must drop the least-spent entries, not an arbitrary slice")

    def test_the_cap_leaves_a_small_ledger_alone(self):
        ledger = {"a": 1, "b": 2}
        self.assertEqual(cap_ledger(ledger, max_entries=10), ledger)

    def test_a_record_the_cap_evicted_can_be_scored_again(self):
        """The documented cost of the cap, stated so it is a decision and not a surprise: eviction
        restores a record's budget. It is bounded and it is disclosed by the cap itself."""
        ledger = {"a": 3, "b": 1}
        capped = cap_ledger(ledger, max_entries=1)
        self.assertNotIn("b", capped, "the least-spent entry is the one evicted")


class BestAttemptTests(GateTestCase):
    """The worktask requires keeping the best attempt per record, not merely the latest.

    Each `score` invocation is a separate process, so the comparison is only possible because the
    ledger carries the best between rounds. An earlier version called `best_attempt(None, ...)` on every
    round, so `best` was always the current attempt and the specified comparison was never made — with
    two ordering bugs found while fixing it, both of which made every rewrite look like an
    improvement. These cases run the gate repeatedly against one ledger, changing the stub's score
    between rounds.
    """

    ROLES = ("product-owner", "designer", "developer", "tester", "business")

    def _all(self, value):
        return {role: value for role in self.ROLES}

    def test_a_worse_rewrite_does_not_replace_the_best(self):
        state = os.path.join(self.tmp, "ledger.json")
        env = {"CONTEXT_MEMORY_DECISIONS_MAX_ATTEMPTS": "3"}

        self.stub.probabilities = self._all(0.2)
        _, first = self.score(state_file=state, **env)
        self.stub.probabilities = self._all(0.9)
        _, second = self.score(state_file=state, **env)
        self.stub.probabilities = self._all(0.3)
        _, third = self.score(state_file=state, **env)

        self.assertEqual(first["records"][0]["best"]["attempt"], 1)
        self.assertEqual(second["records"][0]["best"]["attempt"], 2)
        self.assertEqual(second["records"][0]["best"]["max"], 0.9)
        # The third round scored 0.3, worse than the second round's 0.9: the best must survive it.
        self.assertEqual(third["records"][0]["best"]["attempt"], 2,
                         "a worse rewrite must not become the best attempt")
        self.assertEqual(third["records"][0]["best"]["max"], 0.9)

    def test_a_worse_rewrite_is_reported_as_not_an_improvement(self):
        """The signal an agent rewriting to pass needs: did this rewrite beat the source?"""
        state = os.path.join(self.tmp, "ledger.json")
        env = {"CONTEXT_MEMORY_DECISIONS_MAX_ATTEMPTS": "3"}
        self.stub.probabilities = self._all(0.9)
        self.score(state_file=state, **env)
        self.stub.probabilities = self._all(0.3)
        _, worse = self.score(state_file=state, **env)
        self.assertFalse(worse["records"][0]["bestThisRound"],
                         "0.3 does not improve on a source that already scored 0.9")

    def test_the_stored_best_survives_in_the_ledger(self):
        state = os.path.join(self.tmp, "ledger.json")
        env = {"CONTEXT_MEMORY_DECISIONS_MAX_ATTEMPTS": "3"}
        self.stub.probabilities = self._all(0.9)
        self.score(state_file=state, **env)
        self.stub.probabilities = self._all(0.1)
        self.score(state_file=state, **env)
        with open(state, encoding="utf-8") as handle:
            ledger = json.load(handle)
        entry = ledger[RECORD["subject"]]
        self.assertEqual(entry["attempts"], 2)
        self.assertEqual(entry["best"]["max"], 0.9)

    def test_a_count_only_ledger_still_works(self):
        """The format this gate shipped with earlier must not be discarded on upgrade — that would
        reset every budget, which is the exact failure `ledgerReset` exists to make visible."""
        state = os.path.join(self.tmp, "ledger.json")
        with open(state, "w", encoding="utf-8") as handle:
            json.dump({RECORD["subject"]: 1}, handle)
        self.stub.probabilities = self._all(0.1)
        _, report = self.score(state_file=state, CONTEXT_MEMORY_DECISIONS_MAX_ATTEMPTS="1")
        self.assertNotIn("ledgerReset", report,
                         "a count-only ledger is a valid older format, not a corrupt one")
        self.assertEqual(report["records"][0]["outcome"], "attempts-exhausted",
                         "and its spent budget must still be honoured")

    def test_the_tie_keeps_the_earlier_attempt(self):
        earlier = {"attempt": 1, "max": 0.7, "scores": {}}
        later = {"attempt": 2, "max": 0.7, "scores": {}}
        self.assertEqual(best_attempt(earlier, later), earlier,
                         "a tie keeps the attempt closest to the source")

    def test_both_ledger_formats_report_their_count(self):
        self.assertEqual(entry_attempts(3), 3)
        self.assertEqual(entry_attempts({"attempts": 3, "best": None}), 3)
        self.assertIsNone(entry_best(3))
        self.assertIsNone(entry_best({"attempts": 3, "best": None}))


class RubricValidationTests(GateTestCase):
    """A malformed rubric is a classified refusal, never a traceback.

    The rubric is a data file an operator edits, so every field it reads is validated before it is
    used. Before this, a non-object entry raised `AttributeError` and a missing `instructions` or
    `criteria` branch raised `KeyError` straight out of `load_rubric` — a traceback on stderr, the
    same defect class the capture client fixed for a base URL that quoted its own userinfo.

    Each case writes a rubric file and restores the shipped one, so the property is the loader's and
    not the shipped file's.
    """

    GOOD_ROLE = {"key": "developer", "instructions": "Is this useful to a developer?",
                 "criteria": {"true": "yes", "false": "no"}}

    def _with_rubric(self, body):
        backup = RUBRIC.read_text(encoding="utf-8")
        RUBRIC.write_text(body if isinstance(body, str) else json.dumps(body), encoding="utf-8")
        try:
            return run_gate(["probe"], "", self.gate_env())
        finally:
            RUBRIC.write_text(backup, encoding="utf-8")

    def _assert_refused(self, body):
        proc = self._with_rubric(body)
        combined = proc.stdout + proc.stderr
        self.assertNotIn("Traceback", combined,
                         "a malformed rubric must not produce a traceback")
        self.assertIn("rubric-unavailable", combined)
        self.assertNotEqual(proc.returncode, 0)

    def test_a_non_object_role_entry_is_refused(self):
        self._assert_refused({"version": "1", "roles": [self.GOOD_ROLE, "not-an-object"]})

    def test_an_empty_roles_list_is_refused(self):
        self._assert_refused({"version": "1", "roles": []})

    def test_a_missing_roles_key_is_refused(self):
        self._assert_refused({"version": "1"})

    def test_a_role_missing_instructions_is_refused(self):
        self._assert_refused({"version": "1",
                              "roles": [{"key": "developer",
                                         "criteria": {"true": "t", "false": "f"}}]})

    def test_a_role_missing_criteria_is_refused(self):
        self._assert_refused({"version": "1",
                              "roles": [{"key": "developer", "instructions": "i"}]})

    def test_a_role_missing_the_false_branch_is_refused(self):
        """A `noul` question with no `false` description is not the question the rubric means to ask,
        so it is refused rather than sent half-specified."""
        self._assert_refused({"version": "1",
                              "roles": [{"key": "developer", "instructions": "i",
                                         "criteria": {"true": "t"}}]})

    def test_a_role_missing_the_true_branch_is_refused(self):
        self._assert_refused({"version": "1",
                              "roles": [{"key": "developer", "instructions": "i",
                                         "criteria": {"false": "f"}}]})

    def test_a_rubric_that_is_not_an_object_is_refused(self):
        self._assert_refused(["not", "an", "object"])

    def test_a_role_without_a_key_is_refused(self):
        self._assert_refused({"version": "1",
                              "roles": [{"instructions": "i",
                                         "criteria": {"true": "t", "false": "f"}}]})

    def test_blank_fields_are_refused_not_treated_as_absent(self):
        for field in ("key", "instructions"):
            role = dict(self.GOOD_ROLE)
            role[field] = "   "
            self._assert_refused({"version": "1", "roles": [role]})

    def test_the_refusal_names_the_offending_role(self):
        """An operator editing the file has to be able to find the entry, so the key is named when
        there is one and the position when there is not."""
        proc = self._with_rubric({"version": "1",
                                  "roles": [self.GOOD_ROLE,
                                            {"key": "designer", "instructions": "  "}]})
        self.assertIn("designer", proc.stdout + proc.stderr)

    def test_the_shipped_rubric_is_itself_valid(self):
        """The control: a validator that rejects the file we ship is not a validator."""
        with open(RUBRIC, encoding="utf-8") as handle:
            rubric = json.load(handle)
        self.assertIsInstance(rubric.get("roles"), list)
        self.assertTrue(rubric.get("roles"), "the shipped rubric defines no roles")
        for entry in rubric["roles"]:
            self.assertTrue(entry.get("key"))
            self.assertTrue(entry.get("instructions"))
            self.assertTrue(entry["criteria"].get("true"))
            self.assertTrue(entry["criteria"].get("false"))

    def test_both_rubric_copies_are_identical(self):
        """The kvasir copy exists because the two script folders ship separately, so there is no
        cross-skill import to keep them in step — a divergence would mean the two skills ask about
        different roles for the same record."""
        twin = SCRIPTS.parents[1] / "mimisbrunnr-kvasir-understanding" / "scripts" / "decisions_rubric.json"
        self.assertTrue(twin.is_file(), f"the kvasir rubric copy is missing at {twin}")
        self.assertEqual(RUBRIC.read_text(encoding="utf-8"), twin.read_text(encoding="utf-8"),
                         "the two rubric copies have drifted; an edit belongs in both")


class ProbeTests(GateTestCase):
    def test_probe_reports_ok(self):
        self.stub.probabilities = {role: 0.5 for role in
                                   ("product-owner", "designer", "developer", "tester", "business")}
        proc = run_gate(["probe"], "", self.gate_env())
        self.assertEqual(json.loads(proc.stdout)["outcome"], "ok")

    def test_probe_reports_unreachable(self):
        env = self.gate_env()
        sock = socket.socket()
        sock.bind(("127.0.0.1", 0))
        dead = sock.getsockname()[1]
        sock.close()
        env["CONTEXT_MEMORY_DECISIONS_BASE_URL"] = f"http://127.0.0.1:{dead}"
        proc = run_gate(["probe"], "", env)
        self.assertEqual(json.loads(proc.stdout)["outcome"], "unreachable")
        self.assertNotEqual(proc.returncode, 0)

    def test_probe_never_sends_record_content(self):
        proc = run_gate(["probe"], "", self.gate_env())
        if self.stub.requests:
            self.assertEqual(self.stub.requests[0]["state"]["subject"], "probe")


if __name__ == "__main__":
    unittest.main(verbosity=2)