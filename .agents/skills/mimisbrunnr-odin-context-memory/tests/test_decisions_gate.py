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

import contextlib
import hashlib
import http.server
import io
import json
import os
import re
import shutil
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
# The gate seeds `os.environ` from the machine credential file at import, so the in-process copy is
# loaded against an absent file too — otherwise the operator's decision settings land in this process.
_saved_credential_file = os.environ.get("CONTEXT_MEMORY_CREDENTIAL_FILE")
os.environ["CONTEXT_MEMORY_CREDENTIAL_FILE"] = str(
    Path(tempfile.gettempdir()) / "mimisbrunnr-gate-harness-absent-credentials")
try:
    _spec.loader.exec_module(_gate)
finally:
    if _saved_credential_file is None:
        os.environ.pop("CONTEXT_MEMORY_CREDENTIAL_FILE", None)
    else:
        os.environ["CONTEXT_MEMORY_CREDENTIAL_FILE"] = _saved_credential_file
MAX_LEDGER_ENTRIES = _gate.MAX_LEDGER_ENTRIES
cap_ledger = _gate.cap_ledger
ledger_cap = _gate.ledger_cap
# The end-to-end case crosses the cap through the **real write path** using an injected limit, because
# the path costs one subprocess round trip per record: at the shipped 5000 the suite went from ~40s to
# over 300s, so the only end-to-end case that fitted ran 55 subjects — under the cap, and therefore
# asserting nothing about it. Deleting the `cap_ledger` call from `record_attempt` left every ledger
# test green, which is the mutation `test_the_ledger_is_capped_end_to_end` now fails.
TEST_CAP = 20
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
        if override == "redirect":
            # A 307 keeps the POST and its body, so following it would replay the record — and a
            # hosted endpoint's bearer key — to wherever `Location` points.
            self.send_response(307)
            self.send_header("Location", "/v1/elsewhere")
            self.end_headers()
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


HARNESS_CREDENTIAL_FILE = str(Path(tempfile.gettempdir())
                              / "mimisbrunnr-gate-harness-absent-credentials")


def gate_subprocess_env(env_extra=None):
    """The environment every gate subprocess in this suite runs under."""
    env = os.environ.copy()
    env.pop("CONTEXT_MEMORY_DECISIONS_ENABLED", None)
    for key in list(env):
        if key.startswith("CONTEXT_MEMORY_DECISIONS_"):
            env.pop(key)
    # Point the credential-file lookup at a path that does not exist, unless the case names its own.
    #
    # The gate seeds its settings from this file **at import**, so without this every case in this
    # suite silently inherited the operator's real `~/.mimisbrunnr/credentials` — the one file on the
    # machine describing the machine's real decision configuration. Three failures came from that and
    # none of them looked like it: cases whose stub endpoint was overridden by the file's
    # `BASE_URL` reached for a real decision model and hung, and a case setting the flag to the empty
    # string read as enabled because the file said `true` and an empty value does not count as
    # "already set". A hermetic suite is one whose result depends on the code under test and not on
    # who is running it.
    #
    # Assigned, not `setdefault`: an exported `CONTEXT_MEMORY_CREDENTIAL_FILE` is the same leak by
    # another route, and with one pointing at a populated file 24 cases failed (issue 182). A case that
    # wants a file passes it in `env_extra`.
    env["CONTEXT_MEMORY_CREDENTIAL_FILE"] = HARNESS_CREDENTIAL_FILE
    env.update(env_extra or {})
    return env


def run_gate(args, stdin_text, env_extra=None, timeout=300, gate=GATE):
    proc = subprocess.run([sys.executable, "-B", str(gate), *args],
                          input=stdin_text, capture_output=True, text=True,
                          encoding="utf-8", env=gate_subprocess_env(env_extra), timeout=timeout)
    return proc


def gate_copy(test, redactor=None, rubric=None):
    """A private copy of the gate and its two data files, for cases that need a different redactor or
    rubric. The gate finds both beside itself, so a case swaps them in the copy — never in `scripts/`.
    Swapping the shipped files in place raced with any concurrent run of a suite reading them, and a
    restore from a backup taken mid-swap left a stub as the shipped `redact.py` (issue 182).

    `redactor`: replacement source, or `False` to leave none. `rubric`: replacement body."""
    root = Path(tempfile.mkdtemp())
    test.addCleanup(shutil.rmtree, root, True)
    for source in (GATE, REDACTOR, RUBRIC):
        shutil.copy2(source, root / source.name)
    if redactor is False:
        (root / REDACTOR.name).unlink()
    elif redactor is not None:
        (root / REDACTOR.name).write_text(redactor, encoding="utf-8")
    if rubric is not None:
        (root / RUBRIC.name).write_text(rubric if isinstance(rubric, str) else json.dumps(rubric),
                                        encoding="utf-8")
    return root / GATE.name


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


class ProbabilityRangeTests(GateTestCase):
    """A `noul` outside 0..1 is a malformed answer, never a score (issue 179).

    `json.loads` accepts `NaN` and `Infinity`, and the gate used to clamp, so an `Infinity` became a
    1.0 that clears any bar — the strongest possible score produced by an answer nobody gave.
    """

    ROLES = ("product-owner", "designer", "developer", "tester", "business")

    def _expect_bad_response(self, value):
        self.stub.probabilities = {role: 0.1 for role in self.ROLES}
        self.stub.probabilities["developer"] = value
        proc, report = self.score()
        self.assertEqual(proc.returncode, 0, proc.stderr)
        record = report["records"][0]
        self.assertEqual(record["outcome"], "bad-response")
        self.assertFalse(record["passed"])
        self.assertEqual(record["scores"], {}, "a rejected answer must not carry a score")

    def test_infinity_is_rejected_rather_than_clamped_to_a_pass(self):
        self._expect_bad_response(float("inf"))

    def test_negative_infinity_is_rejected(self):
        self._expect_bad_response(float("-inf"))

    def test_nan_is_rejected(self):
        self._expect_bad_response(float("nan"))

    def test_above_one_is_rejected_rather_than_clamped(self):
        self._expect_bad_response(1.5)

    def test_below_zero_is_rejected_rather_than_clamped(self):
        self._expect_bad_response(-0.1)

    def test_an_integer_too_large_for_a_float_is_rejected_not_a_traceback(self):
        self._expect_bad_response(10 ** 400)

    def test_the_closed_bounds_are_still_scores(self):
        """The control: 0 and 1 are probabilities, so the range check must not reject its own ends."""
        self.stub.probabilities = {role: 0.0 for role in self.ROLES}
        self.stub.probabilities["developer"] = 1
        _, report = self.score()
        record = report["records"][0]
        self.assertEqual(record["outcome"], "scored")
        self.assertEqual(record["scores"]["developer"], 1.0)
        self.assertEqual(record["scores"]["tester"], 0.0)


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

    def test_the_rubric_counts_toward_the_size_limit(self):
        """The guard sized only the record, while the request also carries every role's instructions
        and criteria. A record just under the limit on its own overflows once the rubric rides along,
        and must be held without a request (issue 182)."""
        limit_chars = _gate.MAX_CONTEXT_TOKENS * _gate.CHARS_PER_TOKEN
        base = len(json.dumps(_gate.record_state({**RECORD, "statement": ""}), ensure_ascii=False))
        record = {**RECORD, "statement": "x" * (limit_chars - base - 30)}
        state_alone = json.dumps(_gate.record_state(record), ensure_ascii=False)
        self.assertLessEqual(_gate.estimate_tokens(state_alone), _gate.MAX_CONTEXT_TOKENS,
                             "precondition: the record alone fits, so only the rubric can tip it over")
        _, roles = _gate.load_rubric(_gate.DEFAULT_ROLES.split(","))
        self.assertGreater(len(_gate.encode_request(_gate.record_state(record), roles,
                                                    _gate.DEFAULT_MODEL)),
                           limit_chars, "precondition: the full request is over the limit")

        _, report = self.score([record])
        self.assertEqual(report["records"][0]["outcome"], "oversize")
        self.assertIn("rubric included", report["records"][0]["detail"])
        self.assertEqual(self.stub.requests, [], "an oversize request must never be sent")


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

    def test_parameters_query_or_fragment_on_the_base_are_refused_without_echoing(self):
        """The path is appended after the base, so anything after the origin rides along with every
        request — `;tok=x` reached the stub as part of the path. Refused before any request (issue 182)."""
        for suffix in ("/;tok=s3cretparam", "?tok=s3cretparam", "#s3cretparam"):
            with self.subTest(suffix=suffix):
                proc = run_gate(["probe"], "", self.gate_env(
                    CONTEXT_MEMORY_DECISIONS_BASE_URL=self.stub.base_url + suffix))
                combined = proc.stdout + proc.stderr
                self.assertNotEqual(proc.returncode, 0)
                self.assertIn("bad-decisions-url", combined)
                self.assertNotIn("s3cretparam", combined)
        self.assertEqual(self.stub.requests, [])

    def test_a_malformed_port_is_a_classified_outcome_not_a_traceback(self):
        """`endpoint_origin` read `.port` outside its handler, and the report builds it before the
        guard runs (`probe`) or after every record (`score`), so `:99999` or `:abc` escaped as a
        traceback instead of `bad-decisions-url` — and with the gate off as well (issue 184)."""
        for port in ("99999", "abc"):
            url = f"http://127.0.0.1:{port}"
            with self.subTest(port=port, command="probe"):
                proc = run_gate(["probe"], "", self.gate_env(CONTEXT_MEMORY_DECISIONS_BASE_URL=url))
                self.assertNotIn("Traceback", proc.stderr)
                report = json.loads(proc.stdout)
                self.assertEqual(report["outcome"], "bad-decisions-url")
                self.assertEqual(report["endpoint"], "<unparseable>")
                self.assertNotEqual(proc.returncode, 0)
            with self.subTest(port=port, command="score"):
                proc = run_gate(["score"], json.dumps([RECORD]),
                                self.gate_env(CONTEXT_MEMORY_DECISIONS_BASE_URL=url))
                self.assertNotIn("Traceback", proc.stderr)
                self.assertEqual(proc.returncode, 0, proc.stderr)
                report = json.loads(proc.stdout)
                self.assertEqual(report["endpoint"], "<unparseable>")
                self.assertEqual([r["outcome"] for r in report["records"]], ["bad-decisions-url"])
            for command in ("probe", "score"):
                with self.subTest(port=port, command=command, enabled=False):
                    proc = run_gate([command], json.dumps([RECORD]), self.gate_env(
                        CONTEXT_MEMORY_DECISIONS_BASE_URL=url, CONTEXT_MEMORY_DECISIONS_ENABLED="false"))
                    self.assertNotIn("Traceback", proc.stderr)
                    self.assertEqual(proc.returncode, 0, proc.stderr)
                    self.assertEqual(json.loads(proc.stdout)["outcome"], "disabled")
        self.assertEqual(self.stub.requests, [])

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

    def test_no_gated_credential_from_the_shared_fixture_reaches_the_model_or_the_ledger(self):
        """Every `gate: true` entry of the shared credential fixture, planted in the subject, the
        description and the statement: the model's request never carries the secret, and neither does
        the ledger. Driven from the one list the redactor and Heimdallr harnesses also read (issue 182)."""
        fixture = json.loads((Path(__file__).resolve().parent / "fixtures"
                              / "credential_like.json").read_text(encoding="utf-8"))
        gated = [entry for entry in fixture["credentials"] if entry["gate"]]
        self.assertTrue(gated)
        self.stub.probabilities = {"developer": 0.9}
        for entry in gated:
            for field in ("subject", "description", "statement"):
                with self.subTest(id=entry["id"], field=field):
                    self.stub.server.requests.clear()
                    state = os.path.join(self.tmp, f"ledger-{entry['id']}-{field}.json")
                    text = f"Deploy note {entry['text']} for the pipeline."
                    record = {**RECORD, field: text}
                    if field != "subject":
                        record.pop("subject")
                    if field == "statement":
                        record["description"] = "Deploy note"
                    proc, report = self.score([record], state_file=state)
                    self.assertEqual(proc.returncode, 0, proc.stderr)
                    self.assertEqual(len(self.stub.requests), 1)
                    self.assertNotIn(entry["secret"], json.dumps(self.stub.requests[0]),
                                     "a gated credential reached the decision model")
                    with open(state, encoding="utf-8") as handle:
                        ledger_text = handle.read()
                    self.assertNotIn(entry["secret"], ledger_text)
                    for key in json.loads(ledger_text):
                        self.assertNotIn(entry["secret"], key)

    def test_a_secret_in_a_non_string_field_is_redacted_before_the_model_sees_it(self):
        """A field that arrives as a list or a nested map skipped the redactor — only strings were
        collected — and went to the model as-is (issue 184). Each shape is sent through the real
        gate, with a secret the shipped redactor recognises, and the request must not carry it."""
        self.stub.probabilities = {"developer": 0.9}
        secret = "hunter2nestedsecret77"
        shapes = {
            "list": ["first claim", f"password={secret}"],
            "nested map": {"config": {"password": secret}},
            "list of maps": [{"note": "deploy"}, {"api_key": secret}],
        }
        for label, value in shapes.items():
            for field in ("statement", "contentSummary", "boundaries"):
                with self.subTest(shape=label, field=field):
                    self.stub.server.requests.clear()
                    proc, report = self.score([{**RECORD, field: value}])
                    self.assertEqual(proc.returncode, 0, proc.stderr)
                    self.assertEqual(len(self.stub.requests), 1)
                    sent = json.dumps(self.stub.requests[0])
                    self.assertNotIn(secret, sent, "a secret inside a non-string field reached the model")
                    self.assertTrue(report["redaction"], "the scrub must be reported")

    def test_a_non_string_field_keeps_its_content_rather_than_being_dropped(self):
        """The control: the fix must not 'redact' by emptying the field. A clean list or number still
        reaches the model, serialised, so the record is judged on what it carries."""
        self.stub.probabilities = {"developer": 0.9}
        self.score([{**RECORD, "statement": ["Postgres stores the index", 42]}])
        state = self.stub.requests[0]["state"]
        self.assertIn("Postgres stores the index", state["statement"])
        self.assertIn("42", state["statement"])
        self.assertIsInstance(state["statement"], str)

    def test_a_field_the_redactor_never_saw_is_refused_not_sent(self):
        """Defence in depth behind the serialisation: if the state built for the request ever differs
        from the one sent to the redactor, the uninspected field refuses rather than passing through."""
        from unittest import mock
        calls = {"n": 0}
        real = _gate.record_state

        def drifting(record):
            calls["n"] += 1
            state = real(record)
            if calls["n"] > 1:
                state["statement"] = "password=uninspectedsecret99"
            return state

        with mock.patch.object(_gate, "record_state", drifting):
            with self.assertRaises(_gate.GateError) as caught:
                _gate.redact_records([dict(RECORD)])
        self.assertEqual(caught.exception.outcome, "redactor-unavailable")
        self.assertNotIn("uninspectedsecret99", caught.exception.detail)


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
        proc = run_gate(["score"], json.dumps([RECORD]), self.gate_env(),
                        gate=gate_copy(self, redactor=False))
        self.assertNotEqual(proc.returncode, 0)
        self.assertIn("redactor-unavailable", proc.stderr)
        self.assertEqual(self.stub.requests, [], "no request may be made without redaction")


class RedactorTimeoutTests(unittest.TestCase):
    """A redactor that overruns its budget is a redactor that cannot run (issue 182).

    `subprocess.run(timeout=…)` raises `TimeoutExpired`, which is neither `OSError` nor `ValueError`, so
    it escaped as a traceback; the kvasir caller reads a traceback as a skipped gate and writes the
    record unscored. Socket-free: the redactor call and the model call are both replaced in-process.
    """

    def _run_main(self):
        from unittest import mock
        timeout = subprocess.TimeoutExpired(cmd="redact.py", timeout=_gate.REDACTOR_TIMEOUT_SECONDS)
        stderr = io.StringIO()
        env = {k: v for k, v in os.environ.items() if not k.startswith("CONTEXT_MEMORY_DECISIONS_")}
        env["CONTEXT_MEMORY_DECISIONS_ENABLED"] = "true"
        with mock.patch.dict(os.environ, env, clear=True), \
                mock.patch.object(sys, "argv", ["decisions_gate.py", "score"]), \
                mock.patch.object(sys, "stdin", io.StringIO(json.dumps([RECORD]))), \
                mock.patch("subprocess.run", side_effect=timeout) as run, \
                mock.patch.object(_gate, "call_model") as call_model, \
                contextlib.redirect_stderr(stderr), contextlib.redirect_stdout(io.StringIO()):
            with self.assertRaises(SystemExit) as exited:
                _gate.main()
        return exited.exception.code, stderr.getvalue(), run, call_model

    def test_a_redactor_timeout_is_redactor_unavailable_and_sends_nothing(self):
        code, stderr, run, call_model = self._run_main()
        self.assertEqual(code, 1)
        self.assertEqual(json.loads(stderr)["outcome"], "redactor-unavailable")
        self.assertIn("no request was made", json.loads(stderr)["detail"])
        self.assertEqual(run.call_args.kwargs["timeout"], _gate.REDACTOR_TIMEOUT_SECONDS)
        call_model.assert_not_called()


class RedactorArityTests(GateTestCase):
    """The redaction mapping must be trustworthy in **both** directions.

    An earlier guard caught a redactor returning *more* results than candidates and left the
    under-reporting case open. A redactor that dropped one entry left that field unmapped, so it kept
    its unscrubbed value, the model received it, and the record was scored as though inspected. Every
    case here swaps in a stub redactor, because the shipped one is 1:1 and cannot produce the shape.
    """

    def _run_with_redactor(self, stub_source):
        self.stub.probabilities = {"developer": 0.9}
        return run_gate(["score"], json.dumps([RECORD]), self.gate_env(),
                        gate=gate_copy(self, redactor=stub_source))

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
        """The cap must hold on the **real write path**, driven by a batch that crosses it.

        The previous version of this case asserted the cap's wiring while never reaching it: it wrote
        55 subjects against a shipped cap of 5000, and its own comment said so. Deleting the
        `cap_ledger` call from `record_attempt` therefore left the whole suite green — verified, not
        assumed — while the ledger grew without bound, which is the single defect the cap exists to
        prevent. Crossing 5000 for real needs 5000 subprocess round trips, so the cap is made
        injectable (`ledger_cap`) and this case crosses a small one through the identical code path a
        production run takes.
        """
        self.stub.probabilities = dict(self.LOW)
        state = os.path.join(self.tmp, "ledger.json")
        cap = 20
        subjects = [f"subject-{i}" for i in range(cap + 15)]
        records = [{"subject": s, "description": s, "statement": "x"} for s in subjects]
        proc, report = self.score(records=records, state_file=state,
                                  CONTEXT_MEMORY_DECISIONS_MAX_ATTEMPTS="1",
                                  CONTEXT_MEMORY_DECISIONS_LEDGER_MAX_ENTRIES=str(cap))
        self.assertEqual(proc.returncode, 0, proc.stderr)
        with open(state, encoding="utf-8") as handle:
            ledger = json.load(handle)
        self.assertEqual(len(ledger), cap,
                         "the write path wrote more entries than the cap allows, so the cap is not "
                         "wired into record_attempt")
        self.assertGreater(len(ledger), 0, "the ledger must not be emptied")
        # The batch really did exceed the cap, so this is a crossed bound and not an under-filled one.
        self.assertGreater(len(subjects), cap)

    def test_the_shipped_cap_is_the_default_when_unset(self):
        """The seam must be inert in production: unset means the documented 5000, not a test number."""
        self.stub.probabilities = dict(self.LOW)
        state = os.path.join(self.tmp, "ledger.json")
        self.score(records=[{"subject": "a", "description": "a", "statement": "x"}],
                   state_file=state, CONTEXT_MEMORY_DECISIONS_MAX_ATTEMPTS="1",
                   CONTEXT_MEMORY_DECISIONS_LEDGER_MAX_ENTRIES="")
        with open(state, encoding="utf-8") as handle:
            self.assertEqual(len(json.load(handle)), 1)
        self.assertEqual(ledger_cap(), MAX_LEDGER_ENTRIES)

    def test_a_malformed_ledger_cap_is_refused_rather_than_clamped(self):
        """Same rule as every other numeric setting: a cap that silently becomes a different number is
        one the operator trusts and the system does not honour. Also reachable at import, since the
        value is read per call — so a bad value is a classified refusal, not a traceback."""
        for value in ("0", "-1", "abc", "12.5"):
            with self.subTest(value=value):
                proc = run_gate(["score"], json.dumps([RECORD]),
                                self.gate_env(CONTEXT_MEMORY_DECISIONS_LEDGER_MAX_ENTRIES=value))
                combined = proc.stdout + proc.stderr
                self.assertNotEqual(proc.returncode, 0, f"{value}: a bad cap must not succeed")
                self.assertIn("bad-decisions-config", combined)
                self.assertNotIn("Traceback", combined,
                                 f"{value}: a bad cap must be a refusal, never a crash")

    def test_a_blank_ledger_cap_keeps_the_shipped_default(self):
        """Whitespace is an unset value, not a malformed one — the same rule the other settings follow."""
        self.stub.probabilities = dict(self.LOW)
        proc, report = self.score(CONTEXT_MEMORY_DECISIONS_LEDGER_MAX_ENTRIES="   ")
        self.assertEqual(proc.returncode, 0, proc.stderr)
        self.assertEqual(report["outcome"], "ok")

    def test_cap_ledger_bounds_a_ledger_built_at_the_shipped_limit(self):
        """The helper's own bound, at the shipped value. **Not** the wiring.

        This case used to be named `test_the_write_path_applies_the_cap` and its docstring claimed
        "the real write path", while every line called `cap_ledger` directly and asserted on a file it
        had just written by hand — so it proved the helper bounds a dictionary and nothing about
        `record_attempt` using it. The review flagged exactly that, and it was right. The wiring is
        proved by `test_the_ledger_is_capped_end_to_end`, which crosses the cap through the gate
        itself; keeping a second case whose name asserts the wiring it does not exercise would be the
        same defect wearing a different body. Retained at a distinct name and scope, and `cap_ledger` is
        now reached through its own default rather than a second copy of the limit.
        """
        ledger = {f"s{i}": 1 for i in range(MAX_LEDGER_ENTRIES + 5)}
        self.assertEqual(len(cap_ledger(ledger)), MAX_LEDGER_ENTRIES)
        # Under the bound, nothing is dropped — the cap is a ceiling, not a target.
        small = {f"s{i}": 1 for i in range(10)}
        self.assertEqual(cap_ledger(small), small)

    def test_the_cap_holds_at_many_times_the_limit(self):
        """Pure-function check at a size no HTTP round trip could carry, so the bound is exercised
        well past the cap rather than one entry past it."""
        ledger = {f"s{i}": (i % 7) + 1 for i in range(MAX_LEDGER_ENTRIES * 4)}
        capped = cap_ledger(ledger)
        self.assertEqual(len(capped), MAX_LEDGER_ENTRIES)
        self.assertEqual(max(capped.values()), 2,
                         "the most-spent entries are the ones the cap drops")

    def test_the_cap_drops_the_most_spent_entries(self):
        """Losing the memory of a spent budget is the least harmful entry to lose, so the cap drops
        the highest counts rather than an arbitrary slice. This case used to assert the reverse — the
        shipped sort kept the most-spent entries, contradicting its own docstring (issue 182)."""
        ledger = {f"s{i}": (i % 5) + 1 for i in range(20)}
        capped = cap_ledger(ledger, max_entries=5)
        self.assertEqual(len(capped), 5)
        # Four entries tie at the minimum of 1 and all four are kept, so the fifth slot goes to the
        # lowest count above that -- and the tie among those is broken by key, which makes the
        # eviction deterministic rather than dependent on dict ordering.
        self.assertEqual(sorted(capped.values()), [1, 1, 1, 1, 2])
        self.assertEqual(capped, cap_ledger(dict(reversed(list(ledger.items()))), max_entries=5))

    def test_the_cap_never_evicts_the_key_being_written(self):
        """The defect: a new record enters at attempts=1, the least-spent entry, so at the cap the old
        policy evicted it on the write that counted it — every round was its first, and the budget
        never ran out. The key being written survives whatever its count."""
        ledger = {"a": 1, "b": 1, "new": 3}
        capped = cap_ledger(ledger, max_entries=2, keep="new")
        self.assertIn("new", capped)
        self.assertEqual(len(capped), 2)
        self.assertEqual(cap_ledger({"new": 9}, max_entries=1, keep="new"), {"new": 9})

    def test_the_cap_leaves_a_small_ledger_alone(self):
        ledger = {"a": 1, "b": 2}
        self.assertEqual(cap_ledger(ledger, max_entries=10), ledger)

    def test_a_record_the_cap_evicted_can_be_scored_again(self):
        """The documented cost of the cap, stated so it is a decision and not a surprise: eviction
        restores a record's budget. It is bounded, and `ledgerEvicted` discloses it."""
        ledger = {"a": 3, "b": 1}
        capped = cap_ledger(ledger, max_entries=1)
        self.assertNotIn("a", capped, "the most-spent entry is the one evicted")

    def test_the_budget_holds_at_the_cap_end_to_end(self):
        """Through the real gate at a cap of 2: the record being scored is never the one evicted, so
        round max+1 is `attempts-exhausted`, and every eviction is disclosed (issue 182).

        Run against fillers both more and less spent than the record, so whichever entry the eviction
        order would pick, the record being written is the one it must not pick."""
        self.stub.probabilities = dict(self.LOW)
        env = {"CONTEXT_MEMORY_DECISIONS_MAX_ATTEMPTS": "2",
               "CONTEXT_MEMORY_DECISIONS_LEDGER_MAX_ENTRIES": "2"}
        for filler_attempts in (0, 5):
            with self.subTest(filler_attempts=filler_attempts):
                state = os.path.join(self.tmp, f"ledger-{filler_attempts}.json")
                with open(state, "w", encoding="utf-8") as handle:
                    json.dump({_gate.ledger_key(f"filler-{i}"): {"attempts": filler_attempts,
                                                                 "best": None}
                               for i in (1, 2)}, handle)
                rounds = [self.score(state_file=state, **env)[1] for _ in range(3)]
                self.assertEqual([r["records"][0]["outcome"] for r in rounds],
                                 ["scored", "scored", "attempts-exhausted"])
                self.assertEqual([r["ledgerEvicted"] for r in rounds], [1, 0, 0])
                with open(state, encoding="utf-8") as handle:
                    ledger = json.load(handle)
                self.assertEqual(len(ledger), 2)
                self.assertEqual(ledger[_gate.ledger_key(RECORD["subject"])]["attempts"], 2)

    def test_no_eviction_reports_zero(self):
        self.stub.probabilities = dict(self.LOW)
        _, report = self.score(state_file=os.path.join(self.tmp, "ledger.json"))
        self.assertEqual(report["ledgerEvicted"], 0)


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
        entry = ledger[_gate.ledger_key(RECORD["subject"])]
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


LOOPBACK_SETTINGS = {"base_url": "http://127.0.0.1:11434", "path": "/v1/systemone", "api_key": ""}
HOST_MOVING_PATHS = ("@evil.invalid/v1", "/v1@evil.invalid", "//evil.invalid/v1",
                     "http://evil.invalid/v1", "v1/systemone", "/v1 x", "/v1\\x", "/v1\tx")


class RequestPathGuardTests(unittest.TestCase):
    """A configured path is appended to an already-approved base, so it must not move the request.

    Model-free and socket-free: every refusal happens before a connection is attempted.
    """

    def test_a_path_that_moves_the_authority_is_refused(self):
        for path in HOST_MOVING_PATHS:
            with self.subTest(path=path):
                with self.assertRaises(_gate.GateError) as caught:
                    _gate.resolve_url({**LOOPBACK_SETTINGS, "path": path})
                self.assertEqual(caught.exception.outcome, "bad-decisions-url")
                self.assertNotIn("evil.invalid", caught.exception.detail,
                                 "the refusal must not echo the configured path")

    def test_an_ordinary_path_keeps_the_validated_origin(self):
        self.assertEqual(_gate.resolve_url(LOOPBACK_SETTINGS), "http://127.0.0.1:11434/v1/systemone")

    def test_probe_refuses_a_host_moving_path_before_any_request(self):
        proc = run_gate(["probe"], "", {
            "CONTEXT_MEMORY_DECISIONS_ENABLED": "true",
            "CONTEXT_MEMORY_DECISIONS_BASE_URL": "http://127.0.0.1:9",
            "CONTEXT_MEMORY_DECISIONS_PATH": "@evil.invalid/v1/systemone"})
        report = json.loads(proc.stdout)
        self.assertEqual(report["outcome"], "bad-decisions-url")
        self.assertNotEqual(proc.returncode, 0)
        self.assertNotIn("evil.invalid", proc.stdout + proc.stderr)

    def test_score_marks_every_record_for_a_host_moving_path(self):
        proc = run_gate(["score"], json.dumps([RECORD]), {
            "CONTEXT_MEMORY_DECISIONS_ENABLED": "true",
            "CONTEXT_MEMORY_DECISIONS_BASE_URL": "http://127.0.0.1:9",
            "CONTEXT_MEMORY_DECISIONS_PATH": "//evil.invalid/v1/systemone"})
        self.assertEqual(proc.returncode, 0, proc.stderr)
        self.assertEqual([r["outcome"] for r in json.loads(proc.stdout)["records"]],
                         ["bad-decisions-url"])


class RedirectGuardTests(unittest.TestCase):
    """The opener must refuse a redirect: it would replay the record and the bearer key elsewhere."""

    def test_the_redirect_handler_refuses_with_a_classified_outcome(self):
        request = _gate.urllib.request.Request(
            "https://decisions.example/v1/systemone", headers={"Authorization": "Bearer secret"})
        with self.assertRaises(_gate.GateError) as caught:
            _gate._NoRedirect().redirect_request(request, None, 302, "Found", {},
                                                 "https://evil.example/")
        self.assertEqual(caught.exception.outcome, "redirect-refused")
        self.assertNotIn("secret", caught.exception.detail)

    def test_the_real_opener_installs_the_redirect_and_proxy_guards(self):
        # The handler only guards if `call_model` installs it, so the opener it builds is inspected.
        from unittest.mock import patch

        built = []

        class Sent(Exception):
            pass

        def spy(*handlers):
            built.append(handlers)
            raise Sent()

        with patch.object(_gate.urllib.request, "build_opener", side_effect=spy):
            with self.assertRaises(Sent):
                _gate.call_model({"subject": "probe", "statement": "probe"},
                                 [{"key": "developer", "instructions": "x",
                                   "criteria": {"true": "t", "false": "f"}}],
                                 {**LOOPBACK_SETTINGS, "model": "nimble", "timeout": 1}, "v")
        (handlers,) = built
        self.assertIn(_gate._NoRedirect, handlers)
        proxies = [h for h in handlers if isinstance(h, _gate.urllib.request.ProxyHandler)]
        self.assertEqual([p.proxies for p in proxies], [{}])


    def test_a_redirect_through_the_real_opener_is_refused(self):
        # The opener `call_model` builds, unchanged, with one in-memory transport added ahead of the
        # socket one, so the 307 travels urllib's real redirect machinery without binding a port.
        import email.message
        import io
        import urllib.response
        from unittest.mock import patch

        hits = []

        class Answers307(_gate.urllib.request.BaseHandler):
            handler_order = 100

            def http_open(self, req):
                hits.append(req.full_url)
                headers = email.message.Message()
                headers["Location"] = "/v1/elsewhere"
                response = urllib.response.addinfourl(io.BytesIO(b""), headers, req.full_url, 307)
                response.msg = "Temporary Redirect"
                return response

        real_build = _gate.urllib.request.build_opener
        with patch.object(_gate.urllib.request, "build_opener",
                          side_effect=lambda *handlers: real_build(*handlers, Answers307)):
            with self.assertRaises(_gate.GateError) as caught:
                _gate.call_model({"subject": "probe", "statement": "probe"},
                                 [{"key": "developer", "instructions": "x",
                                   "criteria": {"true": "t", "false": "f"}}],
                                 {**LOOPBACK_SETTINGS, "model": "nimble", "timeout": 1}, "v")
        self.assertEqual(caught.exception.outcome, "redirect-refused")
        self.assertEqual(hits, ["http://127.0.0.1:11434/v1/systemone"],
                         "the redirect target must never be requested")


class RedirectEndToEndTests(GateTestCase):
    def test_a_redirect_from_the_endpoint_is_refused_not_followed(self):
        self.stub.override = "redirect"
        proc, report = self.score()
        self.assertEqual(proc.returncode, 0, proc.stderr)
        self.assertEqual(report["records"][0]["outcome"], "redirect-refused")
        self.assertNotIn("Traceback", proc.stderr)
        self.assertEqual(self.stub.requests, [], "the redirect target must never be reached")


class LedgerPrivacyTests(unittest.TestCase):
    """The ledger is a file on disk, and a subject can carry personal data, so keys are digests.

    Socket-free: the ledger functions are driven directly against a temporary state file.
    """

    SUBJECT = "Onboarding for Jane Example <jane.example@example.com>"

    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.state = os.path.join(self.tmp, "ledger.json")

    def read_raw(self):
        with open(self.state, encoding="utf-8") as handle:
            return handle.read()

    def test_the_ledger_file_holds_a_digest_and_never_the_subject(self):
        attempt, _reset, _best, _evicted = _gate.next_attempt(self.state, self.SUBJECT, 3)
        _gate.record_attempt(self.state, self.SUBJECT, attempt, {"developer": 0.4})
        raw = self.read_raw()
        self.assertNotIn("Jane", raw)
        self.assertNotIn("jane.example@example.com", raw)
        self.assertEqual(list(json.loads(raw)), [_gate.ledger_key(self.SUBJECT)])
        self.assertRegex(_gate.ledger_key(self.SUBJECT), r"^sha256:[0-9a-f]{64}$")

    def test_a_raw_keyed_ledger_is_migrated_and_keeps_its_spent_budget(self):
        # An older gate wrote raw subjects. Dropping them would reset each budget; hashing keeps it,
        # and the rewrite takes the subject off the disk even on the exhausted path, which writes
        # nothing otherwise.
        with open(self.state, "w", encoding="utf-8") as handle:
            json.dump({self.SUBJECT: {"attempts": 2, "best": None}}, handle)
        attempt, reset, _best, _evicted = _gate.next_attempt(self.state, self.SUBJECT, 2)
        self.assertIsNone(attempt, "the spent budget must survive the migration")
        self.assertFalse(reset, "a migration is not a discarded ledger")
        raw = self.read_raw()
        self.assertNotIn("Jane", raw)
        self.assertEqual(json.loads(raw),
                         {_gate.ledger_key(self.SUBJECT): {"attempts": 2, "best": None}})

    def test_a_raw_and_a_digest_key_for_one_record_merge_to_the_larger_count(self):
        key = _gate.ledger_key(self.SUBJECT)
        best = {"attempt": 1, "max": 0.4, "scores": {"developer": 0.4}}
        ledger, migrated = _gate.migrate_ledger({self.SUBJECT: 3, key: {"attempts": 1, "best": best}})
        self.assertTrue(migrated)
        self.assertEqual(ledger, {key: {"attempts": 3, "best": best}})

    def test_a_digest_keyed_ledger_is_not_rewritten_as_a_migration(self):
        ledger = {_gate.ledger_key(self.SUBJECT): 1}
        self.assertEqual(_gate.migrate_ledger(ledger), (ledger, False))


class CredentialLoadingTests(GateTestCase):
    """The gate must see the settings `run.sh` publishes to the machine credential file.

    They are duplicated rather than imported, because `context_memory_client` imports `redact` at module
    scope — importing it here would make the redactor a startup dependency of the gate, and a test that
    substitutes a stub redactor would have the stub execute at import and consume the gate's own stdin.
    That is not hypothetical: the first version of the fix imported it and broke four redactor tests
    exactly that way. So the duplication is guarded by an agreement test instead.
    """

    def _credential_file(self):
        return os.path.join(self.tmp, "credentials")

    def _write(self, body):
        with open(self._credential_file(), "w", encoding="utf-8") as handle:
            handle.write(body)

    def test_the_gate_reads_the_flag_from_the_file(self):
        """The defect itself: the file says enabled, the process environment is clean, and the gate
        still skipped."""
        self._write("CONTEXT_MEMORY_DECISIONS_ENABLED=true\n")
        proc = run_gate(["probe"], "", {
            "CONTEXT_MEMORY_CREDENTIAL_FILE": self._credential_file(),
        })
        self.assertNotEqual(json.loads(proc.stdout)["outcome"], "disabled",
                            "the gate ignored the machine credential file")

    def test_a_real_environment_variable_still_wins(self):
        """Load-only-if-absent, the same precedence the capture client uses."""
        self._write("CONTEXT_MEMORY_DECISIONS_ENABLED=true\n")
        proc = run_gate(["probe"], "", {
            "CONTEXT_MEMORY_CREDENTIAL_FILE": self._credential_file(),
            "CONTEXT_MEMORY_DECISIONS_ENABLED": "false",
        })
        self.assertEqual(json.loads(proc.stdout)["outcome"], "disabled",
                         "the environment must beat the file")

    def test_the_model_and_endpoint_are_read_from_the_file(self):
        self._write("CONTEXT_MEMORY_DECISIONS_MODEL=some-other-model\n"
                    "CONTEXT_MEMORY_DECISIONS_BASE_URL=http://127.0.0.1:9\n")
        proc = run_gate(["probe"], "", {
            "CONTEXT_MEMORY_CREDENTIAL_FILE": self._credential_file(),
        })
        report = json.loads(proc.stdout)
        self.assertEqual(report["model"], "some-other-model")
        self.assertEqual(report["endpoint"], "http://127.0.0.1:9")

    def test_only_the_named_keys_are_seeded(self):
        """A machine file holding an unrelated secret must not pull it into this process — the read
        client's refusal to start with a write token present depends on that discipline.

        The endpoint is the harness's stub, passed explicitly. This case previously passed **only**
        the credential file, so `CONTEXT_MEMORY_DECISIONS_BASE_URL` fell back to its documented default
        of `http://localhost:11434` and the assertion was really "a decision model answered somewhere on
        this developer's machine". It passed locally and failed in CI with `unreachable`, because CI has
        no Ollama. The docstring claimed the endpoint came from the stub; it never did. A test that
        silently depends on a developer's local daemon is not hermetic, and it is green for the wrong
        reason on exactly the machine least able to catch it.
        """
        self._write("CONTEXT_MEMORY_DECISIONS_ENABLED=true\n"
                    "SOME_OTHER_SECRET=should-not-be-loaded\n")
        proc = run_gate(["probe"], "", self.gate_env(
            CONTEXT_MEMORY_CREDENTIAL_FILE=self._credential_file()))
        self.assertEqual(json.loads(proc.stdout)["outcome"], "ok",
                         "enabled from the file, and the stub answered")

        # The case is named for the *exclusion*, and reaching `ok` only proves the inclusion — the
        # named flag was read. Nothing here asserted the unlisted key stayed out, so a loader that
        # seeded every key in the file would have passed.
        #
        # Driven in a subprocess that reports what its own environment holds. Loading the gate
        # in-process was the obvious alternative and it leaked: the loader runs at import, so the
        # unlisted key landed in `os.environ` for the rest of the process, and the sibling
        # loader-agreement test — which passes that same name as a *wanted* key and relies on
        # "a value already in the environment wins" — then diverged. One test's environment became
        # another's input. A subprocess cannot do that.
        probe = ("import os,sys;sys.path.insert(0, %r);"
                 "import importlib.util as u;"
                 "s=u.spec_from_file_location('g', %r);m=u.module_from_spec(s);s.loader.exec_module(m);"
                 "print(repr(os.environ.get('CONTEXT_MEMORY_DECISIONS_ENABLED')),"
                 "repr(os.environ.get('SOME_OTHER_SECRET')))"
                 % (str(SCRIPTS), str(GATE)))
        clean = {k: v for k, v in os.environ.items()
                 if not k.startswith("CONTEXT_MEMORY_DECISIONS_")}
        clean["CONTEXT_MEMORY_CREDENTIAL_FILE"] = self._credential_file()
        result = subprocess.run([sys.executable, "-B", "-c", probe],
                                capture_output=True, text=True, env=clean)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout.strip(), "'true' None",
                         "the named key must be seeded from the file and the unlisted key must not")

    LOADER_KEYS = ("CONTEXT_MEMORY_DECISIONS_ENABLED", "SPACED",
                   "CONTEXT_MEMORY_DECISIONS_MODEL", "SOME_OTHER_SECRET")

    def _loader_result(self, script, wanted):
        """What `script`'s loader seeds from this case's file, read in a fresh process.

        A fresh process because both scripts fix the file path when they are imported: the previous
        in-process version set `CONTEXT_MEMORY_CREDENTIAL_FILE` after import, so both loaders read the
        operator's real file (or none) and agreed on whatever it held — on CI, all-None on both sides
        (issue 182). A script that fails to import is a failure here, never a skip.
        """
        probe = ("import importlib.util as u, json, os, sys\n"
                 "sys.path.insert(0, sys.argv[1])\n"
                 "s = u.spec_from_file_location('loader_under_test', sys.argv[2])\n"
                 "m = u.module_from_spec(s); s.loader.exec_module(m)\n"
                 "m.load_machine_credentials(*json.loads(sys.argv[3]))\n"
                 "print(json.dumps({k: os.environ.get(k) for k in json.loads(sys.argv[4])}))\n")
        env = {k: v for k, v in os.environ.items()
               if k not in self.LOADER_KEYS and not k.startswith("CONTEXT_MEMORY_")}
        env["CONTEXT_MEMORY_CREDENTIAL_FILE"] = self._credential_file()
        proc = subprocess.run(
            [sys.executable, "-B", "-c", probe, str(SCRIPTS), str(script),
             json.dumps(wanted), json.dumps(self.LOADER_KEYS)],
            capture_output=True, text=True, env=env)
        self.assertEqual(proc.returncode, 0,
                         f"{script.name} could not be loaded for comparison: {proc.stderr[-400:]}")
        return json.loads(proc.stdout.strip().splitlines()[-1])

    def test_the_two_loaders_agree(self):
        """The duplication guard: the gate's loader and the capture client's must produce the same
        environment from the same file. This is the test that makes the duplication safe.

        Agreement alone is satisfied by two loaders that both read nothing, so the expected values are
        asserted too — `SPACED` proves the file was really parsed (key and value both trimmed)."""
        self._write("# comment\nCONTEXT_MEMORY_DECISIONS_ENABLED=true\n"
                    "malformed line without a delimiter\n"
                    "  SPACED = value  \nCONTEXT_MEMORY_DECISIONS_MODEL=nimble\n")
        wanted = ["CONTEXT_MEMORY_DECISIONS_ENABLED", "CONTEXT_MEMORY_DECISIONS_MODEL", "SPACED"]
        gate = self._loader_result(GATE, wanted)
        capture = self._loader_result(SCRIPTS / "context_memory_client.py", wanted)
        self.assertEqual(gate, {"CONTEXT_MEMORY_DECISIONS_ENABLED": "true", "SPACED": "value",
                                "CONTEXT_MEMORY_DECISIONS_MODEL": "nimble",
                                "SOME_OTHER_SECRET": None})
        self.assertEqual(capture, gate,
                         "the two loaders diverged; the duplication is only safe while they agree")


class RubricValidationTests(GateTestCase):
    """A malformed rubric is a classified refusal, never a traceback.

    The rubric is a data file an operator edits, so every field it reads is validated before it is
    used. Before this, a non-object entry raised `AttributeError` and a missing `instructions` or
    `criteria` branch raised `KeyError` straight out of `load_rubric` — a traceback on stderr, the
    same defect class the capture client fixed for a base URL that quoted its own userinfo.

    Each case runs a private copy of the gate with its own rubric, so the shipped file is never
    rewritten — see `gate_copy`.
    """

    GOOD_ROLE = {"key": "developer", "instructions": "Is this useful to a developer?",
                 "criteria": {"true": "yes", "false": "no"}}

    def _with_rubric(self, body):
        return run_gate(["probe"], "", self.gate_env(), gate=gate_copy(self, rubric=body))

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


class CalibrationEvidenceTests(unittest.TestCase):
    """The rubric and its threshold were argued from impression, and impression produced the claim
    that the model "saturates toward yes". It does not: on the committed labelled fixture the gate
    scores precision 1.00 and recall 1.00, and two attempts to tighten the rubric both made it
    worse. Those numbers live in the fixture so the next argument starts from them.

    **None of this runs the model.** `score_decisions_calibration.py` is on-demand tooling; the CI
    gate never has a decision model. What CI holds is the *shape* of the recorded run, and above
    all the pin: a recorded run must never certify a rubric it did not score. This skill has
    already shipped that defect once, in the semantic-fixture verdicts, where positional pairing
    made a 1.0/1.0 assertion certify the wrong verdicts with nothing failing.
    """

    @classmethod
    def setUpClass(cls):
        path = Path(__file__).resolve().parent / "fixtures" / "decisions_calibration.json"
        with open(path, encoding="utf-8") as handle:
            cls.doc = json.load(handle)
        cls.records = cls.doc["records"]
        cls.measured = cls.doc["recordedRun"]
        with open(RUBRIC, encoding="utf-8") as handle:
            cls.rubric = json.load(handle)
        cls.roles = {r["key"] for r in cls.rubric["roles"]}

    def test_the_recorded_run_is_pinned_to_the_shipped_rubric(self):
        """The stale-evidence guard. Editing the rubric without re-measuring fails here, because
        the committed numbers describe a rubric that no longer exists — which is how a run comes to
        certify a gate it never scored."""
        self.assertEqual(self.doc["rubricVersion"], self.rubric["version"],
                         "the calibration fixture is pinned to rubric version "
                         f"{self.doc['rubricVersion']} but the shipped rubric is "
                         f"{self.rubric['version']}. Re-measure with "
                         "tests/score_decisions_calibration.py and re-record recordedRun, or revert "
                         "the rubric.")

    def test_the_calibration_fixture_is_pinned_to_a_known_rubric_version(self):
        """The mirror image: a rubric edit that forgets the fixture must fail loudly rather than
        leaving both at version 1 and silently agreeing with each other about nothing."""
        self.assertRegex(str(self.doc["rubricVersion"]), r"^\d+$")
        self.assertRegex(str(self.rubric["version"]), r"^\d+$")

    def test_the_recorded_run_is_pinned_to_the_shipped_rubric_bytes(self):
        """The version pin alone passes an edit that keeps the version, so recorded figures could
        certify instructions or criteria they never scored (issue 179). The digest is of the bytes."""
        shipped = hashlib.sha256(RUBRIC.read_bytes()).hexdigest()
        self.assertEqual(self.doc["rubricSha256"], shipped,
                         "the shipped rubric's bytes differ from the rubric the calibration fixture "
                         "was measured against. Re-measure with tests/score_decisions_calibration.py "
                         "and re-record recordedRun and rubricSha256 together, or revert the rubric.")

    def test_the_recorded_bar_is_the_gate_code_default(self):
        """The fixture called 0.85 the shipped default while DEFAULT_MIN_PROBABILITY was still 0.5,
        so a run without the launcher's credential file gated at a bar nothing measured (issue 179)."""
        self.assertEqual(self.measured["bar"], _gate.DEFAULT_MIN_PROBABILITY,
                         "recordedRun.bar no longer matches DEFAULT_MIN_PROBABILITY; re-measure "
                         "at the new default or correct the fixture")
        self.assertLess(self.measured["tier01MaxBestRole"], self.measured["bar"])
        self.assertGreater(self.measured["tier23MinBestRole"], self.measured["bar"])

    def test_the_primary_bar_is_the_one_the_launcher_publishes(self):
        """`bar` is labelled as the value `scripts/run.sh` writes into the credential file. That
        script exists only in the repository that ships the launcher, not in every consumer."""
        run_sh = Path(__file__).resolve().parents[4] / "scripts" / "run.sh"
        if not run_sh.is_file():
            self.skipTest("scripts/run.sh is not part of this checkout")
        published = re.search(r"^CONTEXT_MEMORY_DECISIONS_MIN_PROBABILITY=(\S+)$",
                              run_sh.read_text(encoding="utf-8"), re.MULTILINE)
        self.assertIsNotNone(published, "run.sh no longer publishes a decision threshold")
        self.assertEqual(float(published.group(1)), self.measured["bar"])

    def test_every_record_declares_a_tier_a_statement_and_an_expectation(self):
        for record in self.records:
            self.assertIn("id", record)
            self.assertIn(record["tier"], (0, 1, 2, 3), record["id"])
            self.assertTrue(record["statement"].strip(), record["id"])
            self.assertIsInstance(record["expect"], list, record["id"])

    def test_a_fixture_id_does_not_name_its_own_verdict(self):
        """The blinding can be defeated by the identifier, and this skill's own fixture set is the
        documented example: two ids there name their expected verdicts. Opaque ids are the guard."""
        for record in self.records:
            self.assertRegex(record["id"], r"^c\d+$",
                             f"fixture id {record['id']!r} is not opaque; a descriptive id can "
                             "reveal the expected verdict to a blinded run")

    def test_every_expectation_names_a_role_the_shipped_rubric_defines(self):
        for record in self.records:
            for role in record["expect"]:
                self.assertIn(role, self.roles,
                              f"{record['id']} expects role {role!r}, which the rubric does not define")

    def test_only_records_without_a_checkable_fact_expect_a_hold(self):
        """The gradient is the whole measurement: tier 0-1 is the hold side and tier 2-3 the pass
        side. A fixture that mixed them could score perfectly while measuring nothing."""
        for record in self.records:
            if record["tier"] <= 1:
                self.assertEqual(record["expect"], [], record["id"])
            else:
                self.assertTrue(record["expect"], record["id"])

    def test_the_fixture_has_both_sides_of_the_decision(self):
        """A gate measured only on records it should pass is a gate with no recall number."""
        junk = [r for r in self.records if r["tier"] <= 1]
        good = [r for r in self.records if r["tier"] >= 2]
        self.assertGreaterEqual(len(junk), 3, "too few hold-side records to measure precision")
        self.assertGreaterEqual(len(good), 3, "too few pass-side records to measure recall")
        self.assertGreaterEqual(len({r["domain"] for r in self.records}), 4,
                                "too few domains; the model's training skews to its own categories")

    def test_the_recorded_confusion_matrix_is_internally_consistent(self):
        """A committed matrix whose parts disagree would let the scorer's own arithmetic be the
        only thing checking it."""
        tp, fp = self.measured["truePositive"], self.measured["falsePositive"]
        tn, fn = self.measured["trueNegative"], self.measured["falseNegative"]
        self.assertEqual(tp + fn, sum(1 for r in self.records if r["tier"] >= 2))
        self.assertEqual(tn + fp, sum(1 for r in self.records if r["tier"] <= 1))
        self.assertEqual(tp + fp + tn + fn, len(self.records))
        self.assertAlmostEqual(self.measured["precision"], tp / (tp + fp) if tp + fp else 0.0, places=3)
        self.assertAlmostEqual(self.measured["recall"], tp / (tp + fn) if tp + fn else 0.0, places=3)

    def test_the_recorded_separation_follows_from_its_own_figures(self):
        self.assertAlmostEqual(
            self.measured["separation"],
            self.measured["tier23MeanBestRole"] - self.measured["tier01MeanBestRole"], places=2)

    def test_the_recorded_bar_sits_inside_the_gap_the_fixture_measured(self):
        """The reason this threshold is provisional is that the gate is bimodal: a bar anywhere in
        the empty band between the hold side and the pass side gives the same verdicts. That is a
        fact about the fixture, so it is checkable rather than a claim."""
        self.assertGreater(self.measured["tier01MaxBestRole"], 0.0, "no hold-side signal to compare")
        self.assertLess(self.measured["tier01MaxBestRole"], self.measured["bar"],
                        "the bar no longer holds every junk record; the recorded run is stale")
        self.assertGreater(self.measured["tier23MinBestRole"], self.measured["bar"],
                           "the bar no longer passes every record with a fact; the run is stale")

    def test_the_recorded_run_declines_the_whole_tautology_before_believing_it(self):
        """The recorded numbers say the shipped rubric separates junk from records-with-a-fact.
        If a future edit makes the roles fire everywhere, this is the number that will have moved,
        and it is here to be read rather than rediscovered."""
        self.assertGreater(self.measured["separation"], 0.5,
                           "a separation this low would mean the rubric is not separating")
        self.assertLessEqual(
            max(self.measured["perRoleClearing"].values()), len(self.records),
            "a role clearing every record carries no negative information, which is exactly the "
            "defect the two rejected variants were measured against")


class CalibrationScorerTests(unittest.TestCase):
    """`score_decisions_calibration.py` needs a model to run end to end, but its arithmetic does not.

    An unscored record carried no scores and was read as a best-role of 0.0, so a failed round
    counted as a hold — a true negative or a false negative, never disclosed (issue 179).
    """

    @classmethod
    def setUpClass(cls):
        spec = _ilu.spec_from_file_location(
            "_score_decisions_calibration_under_test",
            Path(__file__).resolve().parent / "score_decisions_calibration.py")
        cls.scorer = _ilu.module_from_spec(spec)
        spec.loader.exec_module(cls.scorer)
        cls.doc = {"records": [
            {"id": "c01", "tier": 0, "expect": [], "statement": "junk"},
            {"id": "c02", "tier": 2, "expect": ["developer"], "statement": "a fact"},
        ]}

    def report(self, *records):
        return {"rubricVersion": "2", "model": "nimble", "endpoint": "http://127.0.0.1",
                "records": list(records)}

    @staticmethod
    def scored(identity, developer):
        return {"identity": identity, "outcome": "scored",
                "scores": {"developer": developer, "tester": 0.1}}

    def run_score(self, report):
        with contextlib.redirect_stdout(io.StringIO()):
            return self.scorer.score(self.doc, report, 0.5)

    def test_a_fully_scored_report_is_scored(self):
        """The control: the refusals below are about unscored records, not about scoring at all."""
        result = self.run_score(self.report(self.scored("c01", 0.1), self.scored("c02", 0.9)))
        self.assertEqual((result["truePositive"], result["trueNegative"]), (1, 1))

    def test_an_unscored_pass_side_record_is_refused_not_counted_as_a_false_negative(self):
        failed = {"identity": "c02", "outcome": "unreachable", "scores": {}}
        with self.assertRaises(SystemExit) as caught:
            self.run_score(self.report(self.scored("c01", 0.1), failed))
        self.assertIn("c02", str(caught.exception))
        self.assertIn("unreachable", str(caught.exception))

    def test_an_unscored_hold_side_record_is_refused_not_counted_as_a_true_negative(self):
        failed = {"identity": "c01", "outcome": "bad-response", "scores": {}}
        with self.assertRaises(SystemExit):
            self.run_score(self.report(failed, self.scored("c02", 0.9)))

    def test_a_report_missing_a_record_is_refused(self):
        with self.assertRaises(SystemExit):
            self.run_score(self.report(self.scored("c01", 0.1)))

    def test_a_misaligned_report_is_refused(self):
        with self.assertRaises(SystemExit):
            self.run_score(self.report(self.scored("c02", 0.9), self.scored("c01", 0.1)))

    # --- the comparison against the recorded run (issue 182) ------------------------------------

    def _fixture(self):
        with open(Path(__file__).resolve().parent / "fixtures" / "decisions_calibration.json",
                  encoding="utf-8") as handle:
            return json.load(handle)

    def _collapsed_report(self, doc):
        """Every verdict and role count of the recorded run at 0.85 — 15/0/6/0, 24/30 expected, 17
        unexpected — reproduced by scores that sit either side of the bar at 0.84 and 0.86. The gap the
        threshold rests on is gone, so a comparison that certifies this is certifying a different gate."""
        roles = ["product-owner", "designer", "developer", "tester", "business"]
        drop, bleed, records = 6, 17, []
        for fixture in doc["records"]:
            if fixture["tier"] < 2:
                scores = {role: 0.84 for role in roles}
            else:
                scores = {}
                for role in roles:
                    if role in fixture["expect"]:
                        clearing = sum(1 for v in scores.values() if v > 0.85)
                        if drop and len(fixture["expect"]) > 1 and clearing:
                            scores[role], drop = 0.84, drop - 1
                        else:
                            scores[role] = 0.86
                    elif bleed:
                        scores[role], bleed = 0.86, bleed - 1
                    else:
                        scores[role] = 0.84
            records.append({"identity": fixture["id"], "outcome": "scored", "scores": scores})
        return {"rubricVersion": doc["rubricVersion"], "model": "nimble",
                "endpoint": "http://localhost:11434", "records": records}

    def _compare(self, doc, report, threshold):
        with contextlib.redirect_stdout(io.StringIO()):
            measured = self.scorer.score(doc, report, threshold)
            return self.scorer.compare(doc["recordedRun"], measured, threshold, report)

    def test_a_collapsed_distribution_is_not_certified_by_matching_verdicts(self):
        doc = self._fixture()
        report = self._collapsed_report(doc)
        with contextlib.redirect_stdout(io.StringIO()):
            measured = self.scorer.score(doc, report, 0.85)
        for key in ("truePositive", "trueNegative", "expectedRolesClearing", "unexpectedRolesClearing"):
            self.assertEqual(measured[key], doc["recordedRun"][key],
                             f"precondition: {key} matches, so only the shape can tell the runs apart")
        self.assertLess(measured["separation"], 0.1, "the collapsed gap must be measured, not inferred")
        drifted = self._compare(doc, report, 0.85)
        for key in ("separation", "tier01MaxBestRole", "tier23MinBestRole"):
            self.assertIn(key, drifted)

    def test_a_recorded_figure_this_run_did_not_measure_is_drift(self):
        doc = self._fixture()
        with contextlib.redirect_stdout(io.StringIO()):
            drifted = self.scorer.compare(doc["recordedRun"], {"truePositive": 15}, 0.85)
        self.assertIn("separation", drifted)
        self.assertIn("precision", drifted)

    def test_a_bar_with_no_recorded_run_is_not_compared(self):
        """Overriding the bar used to compare against the run recorded at a different one."""
        doc = self._fixture()
        self.assertEqual(self._compare(doc, self._collapsed_report(doc), 0.7), ["bar"])

    def test_the_recorded_former_default_is_compared_at_its_own_bar(self):
        doc = self._fixture()
        block = self.scorer.recorded_at(doc["recordedRun"], 0.5)
        self.assertIs(block, doc["recordedRun"]["atBar05"])
        self.assertIs(self.scorer.recorded_at(doc["recordedRun"], 0.85), doc["recordedRun"])

    def test_a_different_model_is_drift(self):
        doc = self._fixture()
        report = {**self._collapsed_report(doc), "model": "another-model"}
        self.assertIn("model", self._compare(doc, report, 0.85))

    def test_the_distribution_figures_follow_from_the_scores(self):
        doc = {"records": [{"id": "c01", "tier": 0, "expect": []},
                           {"id": "c02", "tier": 1, "expect": []},
                           {"id": "c03", "tier": 2, "expect": ["developer"]}]}
        report = {"records": [
            {"scores": {"developer": 0.1, "tester": 0.2}},
            {"scores": {"developer": 0.3, "tester": 0.0}},
            {"scores": {"developer": 0.9, "tester": 0.95}}]}
        shape = self.scorer.distribution(doc, report, ["developer", "tester"], 0.5)
        self.assertEqual((shape["tier01MeanBestRole"], shape["tier01MaxBestRole"]), (0.25, 0.3))
        self.assertEqual((shape["tier23MeanBestRole"], shape["tier23MinBestRole"]), (0.95, 0.95))
        self.assertEqual(shape["separation"], 0.7)
        self.assertEqual(shape["perRoleClearing"], {"developer": 1, "tester": 1})
        self.assertEqual(shape["maxCrossRolePair"], "developer/tester")


class CalibrationIsolationTests(GateTestCase):
    """The calibration run measures the shipped gate, not the operator's configuration of it.

    `run_gate` copied `os.environ`, so an exported `CONTEXT_MEMORY_DECISIONS_*` setting reached the gate,
    and so did every unset one the gate seeds at import from `CONTEXT_MEMORY_CREDENTIAL_FILE` — a
    recurrence of issue 179 finding 41 (issue 184). Driven end to end through the real gate against the
    stub, with a hostile credential file and a hostile environment, because asserting the env dict alone
    would pass with a gate that still read the file.
    """

    SHIPPED_ROLES = ["product-owner", "designer", "developer", "tester", "business"]
    HOSTILE = {
        "CONTEXT_MEMORY_DECISIONS_ROLES": "tester",
        "CONTEXT_MEMORY_DECISIONS_BELOW_THRESHOLD": "mark",
        "CONTEXT_MEMORY_DECISIONS_MAX_ATTEMPTS": "1",
        "CONTEXT_MEMORY_DECISIONS_API_KEY": "synthetic-operator-key-not-a-secret",
        "CONTEXT_MEMORY_DECISIONS_LEDGER_MAX_ENTRIES": "1",
    }

    @classmethod
    def setUpClass(cls):
        spec = _ilu.spec_from_file_location(
            "_score_decisions_calibration_isolation",
            Path(__file__).resolve().parent / "score_decisions_calibration.py")
        cls.scorer = _ilu.module_from_spec(spec)
        spec.loader.exec_module(cls.scorer)

    def setUp(self):
        super().setUp()
        self.stub.probabilities = {role: 0.9 for role in self.SHIPPED_ROLES}
        saved = {key: value for key, value in os.environ.items()
                 if key.startswith("CONTEXT_MEMORY_DECISIONS_") or key == "CONTEXT_MEMORY_CREDENTIAL_FILE"}

        def restore():
            for key in [k for k in os.environ
                        if k.startswith("CONTEXT_MEMORY_DECISIONS_") or k == "CONTEXT_MEMORY_CREDENTIAL_FILE"]:
                del os.environ[key]
            os.environ.update(saved)
        self.addCleanup(restore)
        for key in saved:
            del os.environ[key]

    def hostile_credential_file(self):
        path = Path(self.tmp) / "credentials"
        lines = [f"{key}={value}" for key, value in self.HOSTILE.items()]
        lines[0] = "CONTEXT_MEMORY_DECISIONS_ROLES=developer"
        lines.append("CONTEXT_MEMORY_DECISIONS_PATH=/v1/operator-path")
        lines.append("CONTEXT_MEMORY_DECISIONS_TIMEOUT=1")
        path.write_text("\n".join(lines) + "\n", encoding="utf-8")
        return str(path)

    def calibrate(self):
        doc = {"records": [{"id": "c01", "statement": "PostgreSQL is the storage engine."},
                           {"id": "c02", "statement": "Exports are capped at twenty records."}]}
        report = self.scorer.run_gate(doc, 0.85, self.stub.base_url, "nimble")
        self.scorer.require_scored(doc, report)
        return report

    def assert_shipped_configuration(self, report):
        for record in report["records"]:
            self.assertEqual(sorted(record["scores"]), sorted(self.SHIPPED_ROLES))
        self.assertEqual(len(self.stub.requests), 2)
        for body in self.stub.requests:
            self.assertEqual(sorted(body["questions"]), sorted(self.SHIPPED_ROLES))

    def test_a_hostile_credential_file_does_not_configure_the_calibration(self):
        os.environ["CONTEXT_MEMORY_CREDENTIAL_FILE"] = self.hostile_credential_file()
        self.assert_shipped_configuration(self.calibrate())

    def test_exported_decision_settings_do_not_configure_the_calibration(self):
        os.environ.update(self.HOSTILE)
        self.assert_shipped_configuration(self.calibrate())

    def test_the_credential_pointer_is_redirected_even_when_unset(self):
        """Unset is not isolated: the gate falls back to `~/.mimisbrunnr/credentials`."""
        env = self.scorer.gate_environment(0.85, "http://127.0.0.1:1", "nimble", base={
            "PATH": "/usr/bin", **self.HOSTILE})
        self.assertEqual(env["CONTEXT_MEMORY_CREDENTIAL_FILE"], os.devnull)
        self.assertEqual(sorted(k for k in env if k.startswith("CONTEXT_MEMORY_DECISIONS_")),
                         sorted(["CONTEXT_MEMORY_DECISIONS_ENABLED", "CONTEXT_MEMORY_DECISIONS_BASE_URL",
                                 "CONTEXT_MEMORY_DECISIONS_MODEL",
                                 "CONTEXT_MEMORY_DECISIONS_MIN_PROBABILITY"]))
        self.assertEqual(env["PATH"], "/usr/bin")


class DiscriminationDisclosureTests(GateTestCase):
    """`passingRoles` becomes `audience:*` tags downstream, and on its own it cannot tell a tie from
    a clear win. Measured on the calibration fixture, 3 of 21 records carry roles scoring exactly
    `1.00000`, and the mean best-role is 0.99 — saturation at the top is the normal case, not the
    exception, so a tag set of three is usually ambiguous rather than thrice-confirmed.
    """

    def test_an_exact_tie_reports_a_zero_margin_and_names_every_tied_role(self):
        self.stub.probabilities = {"product-owner": 1.0, "designer": 0.2, "developer": 1.0,
                                   "tester": 1.0, "business": 0.05}
        rec = json.loads(run_gate(["score"], json.dumps([RECORD]), self.gate_env()).stdout)["records"][0]
        self.assertEqual(rec["discrimination"]["margin"], 0.0)
        self.assertEqual(rec["discrimination"]["tied"], ["developer", "product-owner", "tester"])

    def test_every_role_at_one_is_a_five_way_tie_not_a_sweep(self):
        """A unanimous top is still a tie, and reporting it as a margin of 1.0 would claim a
        discrimination that did not happen. The first version of this test asserted 1.0."""
        self.stub.probabilities = {role: 1.0 for role in
                                   ("product-owner", "designer", "developer", "tester", "business")}
        rec = json.loads(run_gate(["score"], json.dumps([RECORD]), self.gate_env()).stdout)["records"][0]
        self.assertEqual(rec["discrimination"]["margin"], 0.0)
        self.assertEqual(len(rec["discrimination"]["tied"]), 5)

    def test_a_clear_winner_reports_the_gap_to_the_runner_up(self):
        self.stub.probabilities = {"product-owner": 0.9, "designer": 0.1, "developer": 0.2,
                                   "tester": 0.3, "business": 0.05}
        rec = json.loads(run_gate(["score"], json.dumps([RECORD]), self.gate_env()).stdout)["records"][0]
        self.assertAlmostEqual(rec["discrimination"]["margin"], 0.6, places=6)
        self.assertEqual(rec["discrimination"]["tied"], ["product-owner"])

    def test_the_disclosure_never_changes_a_verdict(self):
        """The property that makes this safe to add. A tie and a sweep must agree on `passed` and on
        `passingRoles`, or a reporting field would be deciding something."""
        for probs in (
            {"product-owner": 1.0, "designer": 1.0, "developer": 1.0, "tester": 1.0, "business": 1.0},
            {"product-owner": 0.9, "designer": 0.1, "developer": 0.1, "tester": 0.1, "business": 0.1},
            {"product-owner": 0.1, "designer": 0.1, "developer": 0.1, "tester": 0.1, "business": 0.1},
        ):
            self.stub.probabilities = probs
            rec = json.loads(run_gate(["score"], json.dumps([RECORD]), self.gate_env()).stdout)["records"][0]
            # Compared as a set: `passingRoles` arrives in rubric order, which is the order the
            # operator configured, and asserting sorted() would test the ordering rather than the
            # membership this case is about.
            expected = {r for r, v in probs.items() if v > 0.5}
            self.assertEqual(set(rec["passingRoles"]), expected)
            self.assertEqual(rec["passed"], bool(expected))

    def test_a_failed_round_discloses_zero_rather_than_omitting_the_field(self):
        """A consumer reading `discrimination.margin` must not KeyError on exactly the records whose
        gate outcome is already the thing it most needs to reason about."""
        env = self.gate_env()
        sock = socket.socket()
        sock.bind(("127.0.0.1", 0))
        dead = sock.getsockname()[1]
        sock.close()
        env["CONTEXT_MEMORY_DECISIONS_BASE_URL"] = f"http://127.0.0.1:{dead}"
        rec = json.loads(run_gate(["score"], json.dumps([RECORD]), env).stdout)["records"][0]
        self.assertEqual(rec["discrimination"], {"margin": 0.0, "tied": []})

    def test_a_single_role_reports_its_own_score_as_the_margin(self):
        """Degenerate but reachable via CONTEXT_MEMORY_DECISIONS_ROLES: with one role there is no
        runner-up, and reporting 0.0 would read as a tie that did not happen."""
        self.stub.probabilities = {"business": 0.7}
        env = self.gate_env()
        env["CONTEXT_MEMORY_DECISIONS_ROLES"] = "business"
        rec = json.loads(run_gate(["score"], json.dumps([RECORD]), env).stdout)["records"][0]
        self.assertAlmostEqual(rec["discrimination"]["margin"], 0.7, places=6)
        # One role is trivially tied with itself; `tied` names it rather than claiming a field.
        self.assertEqual(rec["discrimination"]["tied"], ["business"])


class HarnessIsolationTests(GateTestCase):
    """The suite's result must depend on the code under test, not on who is running it.

    These three defects shared one cause: the gate seeds its settings from the machine credential file
    at import, and the harness scrubbed the settings from the *environment* without redirecting the
    file *pointer*. Every case therefore inherited the operator's real configuration. It is worth a
    class of its own because the symptom is invisible by construction — the tests that break are the
    ones that touch a stub endpoint or an empty setting, so the suite still looks comprehensive.
    """

    def test_a_case_does_not_inherit_the_operators_credential_file(self):
        """A file that says enabled and points somewhere real must not reach a case that asked for
        neither. `self.gate_env` sets the endpoint to the stub; if the operator's file won, the stub is
        never reached and the assertion below is testing the operator's machine."""
        operator_file = os.path.expanduser("~/.mimisbrunnr/credentials")
        if not os.path.isfile(operator_file):
            self.skipTest("no operator credential file on this machine, so there is nothing to leak")
        proc, report = self.score()
        self.assertEqual(report["endpoint"], self.stub.base_url,
                         "the gate used an endpoint other than the stub, so the operator's credential "
                         "file reached this case")
        self.assertEqual(len(self.stub.requests), 1, "the stub served no request")

    def test_an_empty_setting_is_still_empty_without_an_operator_file(self):
        """The empty value is a case, not an absence. It reads as disabled because the flag is the
        exact string `true` — and it only reads that way if nothing seeded it behind the case's back."""
        proc, report = self.score(CONTEXT_MEMORY_DECISIONS_ENABLED="")
        self.assertEqual(report["outcome"], "disabled",
                         "an empty flag must not be seeded from anywhere")

    def test_the_harness_points_the_credential_file_at_a_path_that_does_not_exist(self):
        """The isolation itself, asserted on the environment `run_gate` actually builds. The previous
        version built its own environment and checked it echoed back, so it passed with the isolation
        removed (issue 182)."""
        self.assertFalse(os.path.exists(HARNESS_CREDENTIAL_FILE),
                         "the harness's isolation path exists, so it would be read as a real file")
        self.assertEqual(gate_subprocess_env()["CONTEXT_MEMORY_CREDENTIAL_FILE"],
                         HARNESS_CREDENTIAL_FILE)

    def test_an_exported_credential_file_does_not_reach_a_case(self):
        """An operator who exported `CONTEXT_MEMORY_CREDENTIAL_FILE` must not configure the suite. The
        hostile file enables the gate, raises the bar above every stub score and narrows the roles —
        each of which would turn this case's pass into a fail if it leaked (issue 182)."""
        hostile = os.path.join(self.tmp, "exported-credentials")
        with open(hostile, "w", encoding="utf-8") as handle:
            handle.write("CONTEXT_MEMORY_DECISIONS_ENABLED=true\n"
                         "CONTEXT_MEMORY_DECISIONS_BASE_URL=http://127.0.0.1:9\n"
                         "CONTEXT_MEMORY_DECISIONS_MIN_PROBABILITY=0.99\n"
                         "CONTEXT_MEMORY_DECISIONS_ROLES=tester\n")
        self.stub.probabilities = {"developer": 0.9}
        saved = os.environ.get("CONTEXT_MEMORY_CREDENTIAL_FILE")
        os.environ["CONTEXT_MEMORY_CREDENTIAL_FILE"] = hostile
        try:
            proc, report = self.score()
            _, empty = self.score(CONTEXT_MEMORY_DECISIONS_ENABLED="")
        finally:
            if saved is None:
                os.environ.pop("CONTEXT_MEMORY_CREDENTIAL_FILE", None)
            else:
                os.environ["CONTEXT_MEMORY_CREDENTIAL_FILE"] = saved
        self.assertEqual(proc.returncode, 0, proc.stderr)
        self.assertEqual(report["endpoint"], self.stub.base_url)
        self.assertEqual(report["minProbability"], _gate.DEFAULT_MIN_PROBABILITY)
        self.assertTrue(report["records"][0]["passed"])
        self.assertEqual(empty["outcome"], "disabled")


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


_SHIPPED = {path: hashlib.sha256(path.read_bytes()).hexdigest() for path in (GATE, REDACTOR, RUBRIC)}


def tearDownModule():
    """No case may leave a shipped script or data file changed: a suite that rewrites what it tests
    corrupts any concurrent run, and a crash mid-swap leaves the stub in place (issue 182)."""
    changed = [path.name for path, digest in _SHIPPED.items()
               if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != digest]
    if changed:
        raise AssertionError(f"the suite left shipped file(s) changed: {', '.join(changed)}")


if __name__ == "__main__":
    unittest.main(verbosity=2)