#!/usr/bin/env python3
"""Behaviour harness for the ai-understanding decision gate, as wired by `gate_decisions`.

Covers the acceptance criteria the gate's verdict must meet when `ai-understanding --export` uses it:
absent settings make no call and write nothing; env-only and file-only settings both enable; a 0.9
score passes, an all-below-bar unit is held under `hold` and marked under `mark`; `--all` never bypasses
the threshold; the attempt budget is enforced by a ledger through an injectable seam; oversize,
unreachable and malformed responses are classified and never read as a score; a planted secret never
reaches the model; and the 5 `DEDUP_*` keys leave behaviour unchanged.

Run: python3 -B .agents/skills/ai-understanding/tests/gate_behavior.py
"""

from __future__ import annotations

import json
import os
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))

import _gate_harness as h  # noqa: E402
import gate_decisions as gd  # noqa: E402

DEDUP_KEYS = {
    "CONTEXT_MEMORY_DECISIONS_DEDUP_ENABLED": "false",
    "CONTEXT_MEMORY_DECISIONS_DEDUP_MIN_PROBABILITY": "0.5",
    "CONTEXT_MEMORY_DECISIONS_DEDUP_MAX_PAIRS_PER_CANDIDATE": "3",
    "CONTEXT_MEMORY_DECISIONS_DEDUP_MAX_PAIRS_PER_BATCH": "20",
    "CONTEXT_MEMORY_DECISIONS_DEDUP_MAX_RESULTS": "5",
}


def enabled_env(stub, **extra):
    base = {
        "CONTEXT_MEMORY_DECISIONS_ENABLED": "true",
        "CONTEXT_MEMORY_DECISIONS_BASE_URL": stub.base_url,
        "CONTEXT_MEMORY_DECISIONS_MODEL": "nimble",
        "CONTEXT_MEMORY_DECISIONS_MAX_ATTEMPTS": "3",
    }
    base.update(extra)
    return h.gate_subprocess_env(base)


def unit(description="A decision was made", answer="We chose PostgreSQL.", why="AGE decided it.",
         scope="portable", boundaries=""):
    return gd.unit_record({
        "description": description, "answer": answer, "why": why,
        "scope": scope, "boundaries": boundaries,
    })


def fresh_ledger():
    # A unique ledger per single-shot score call, so the shared default budget cannot leak an
    # identity's spent attempts between tests (or between an advisory and a real run).
    return Path(tempfile.mkdtemp()) / "ledger.json"


class SettingsTests(unittest.TestCase):
    def test_absent_settings_probe_is_not_configured(self):
        ok, reason, report = gd.gate_status(env=h.gate_subprocess_env())
        self.assertFalse(ok, reason)
        self.assertEqual(report, "decisions: not configured")

    def test_disabled_gate_makes_no_network_call(self):
        stub = h.Stub()
        self.addCleanup(stub.close)
        # BASE_URL points at the stub, but ENABLED is unset — the gate must return "disabled"
        # without touching the endpoint.
        env = h.gate_subprocess_env({"CONTEXT_MEMORY_DECISIONS_BASE_URL": stub.base_url})
        payload = gd.score([unit()], ledger=fresh_ledger(), env=env)
        self.assertEqual(payload["outcome"], "disabled")
        self.assertEqual(payload["records"], [])
        self.assertEqual(stub.requests, [], "a disabled gate made a network call")

    def test_env_only_settings_enable(self):
        stub = h.Stub()
        self.addCleanup(stub.close)
        ok, reason, _ = gd.gate_status(env=enabled_env(stub))
        self.assertTrue(ok, reason)

    def test_a_credential_file_never_enables(self):
        # The settings are environment variables only. A credential file that says enabled and names a
        # live stub must leave the gate off — the file used to win whenever the environment was not
        # inherited, which replaced an operator's own endpoint with the launcher's defaults.
        stub = h.Stub()
        self.addCleanup(stub.close)
        cf = Path(tempfile.mkdtemp()) / "credentials"
        cf.write_text("CONTEXT_MEMORY_DECISIONS_ENABLED=true\nCONTEXT_MEMORY_DECISIONS_BASE_URL=%s\n"
                      % stub.base_url, encoding="utf-8")
        ok, reason, line = gd.gate_status(env=h.gate_subprocess_env({"CONTEXT_MEMORY_CREDENTIAL_FILE": str(cf)}))
        self.assertFalse(ok)
        self.assertEqual(reason, "not-configured", line)
        self.assertEqual(stub.requests, [], "the gate reached the endpoint named only in the file")


class VerdictTests(unittest.TestCase):
    def _stub(self, probabilities):
        stub = h.Stub()
        self.addCleanup(stub.close)
        stub.probabilities = probabilities
        return stub

    def test_a_09_for_one_role_passes_and_writes(self):
        stub = self._stub({"developer": 0.9, "tester": 0.1, "product-owner": 0.1})
        payload = gd.score([unit()], ledger=fresh_ledger(), env=enabled_env(stub))
        r = payload["records"][0]
        self.assertEqual(r["outcome"], "scored")
        self.assertTrue(r["passed"])
        self.assertIn("developer", r["passingRoles"])
        self.assertEqual(gd.classify(r, "hold", payload["maxAttempts"]), (gd.WRITE, None))

    def test_all_roles_below_the_bar_hold_under_hold(self):
        stub = self._stub({"developer": 0.2, "tester": 0.3, "product-owner": 0.1})
        env = enabled_env(stub, **{"CONTEXT_MEMORY_DECISIONS_MAX_ATTEMPTS": "1"})
        payload = gd.score([unit()], ledger=fresh_ledger(), env=env)
        r = payload["records"][0]
        self.assertFalse(r["passed"])
        self.assertEqual(payload["belowThreshold"], "hold")
        self.assertEqual(gd.classify(r, payload["belowThreshold"], payload["maxAttempts"]),
                         (gd.HELD, None))

    def test_all_roles_below_the_bar_mark_under_mark(self):
        stub = self._stub({"developer": 0.2, "tester": 0.3, "product-owner": 0.1})
        env = enabled_env(stub, **{"CONTEXT_MEMORY_DECISIONS_MAX_ATTEMPTS": "1",
                                   "CONTEXT_MEMORY_DECISIONS_BELOW_THRESHOLD": "mark"})
        payload = gd.score([unit()], ledger=fresh_ledger(), env=env)
        r = payload["records"][0]
        self.assertEqual(payload["belowThreshold"], "mark")
        self.assertEqual(gd.classify(r, payload["belowThreshold"], payload["maxAttempts"]),
                         (gd.MARK, None))

    def test_all_does_not_bypass_the_threshold(self):
        # LADR-014: `--all` is scope coverage, not a bar loosen. `classify` has no `--all` parameter,
        # so the decision is identical under a full re-export. Pin it: a below-bar unit stays
        # held/mark, never forced to write.
        result = {"outcome": "scored", "passed": False, "attempt": 3}
        self.assertEqual(gd.classify(result, "hold", 3), (gd.HELD, None))
        self.assertEqual(gd.classify(result, "mark", 3), (gd.MARK, None))
        self.assertNotEqual(gd.classify(result, "hold", 3), (gd.WRITE, None))

    def test_attempt_cap_crossed_via_injectable_seam(self):
        stub = self._stub({"developer": 0.1, "tester": 0.1})
        env = enabled_env(stub, **{"CONTEXT_MEMORY_DECISIONS_MAX_ATTEMPTS": "2"})
        ledger = Path(tempfile.mkdtemp()) / "ledger.json"
        r1 = gd.score([unit()], ledger=ledger, env=env)["records"][0]
        self.assertEqual((r1["outcome"], r1["attempt"]), ("scored", 1))
        r2 = gd.score([unit()], ledger=ledger, env=env)["records"][0]
        self.assertEqual(r2["attempt"], 2)
        r3 = gd.score([unit()], ledger=ledger, env=env)["records"][0]
        self.assertEqual(r3["outcome"], "attempts-exhausted")
        self.assertEqual(gd.classify(r3, "hold", 2), (gd.HELD, None))

    def test_attempt_seam_is_inert_when_unset(self):
        stub = self._stub({"developer": 0.1, "tester": 0.1})
        env = enabled_env(stub)  # MAX_ATTEMPTS not overridden -> default 3
        payload = gd.score([unit()], ledger=fresh_ledger(), env=env)
        self.assertEqual(payload["maxAttempts"], 3)


class FailureShapeTests(unittest.TestCase):
    def _stub(self, **kw):
        stub = h.Stub()
        self.addCleanup(stub.close)
        stub.probabilities = kw.pop("probabilities", {"developer": 0.9, "tester": 0.1})
        stub.override = kw.pop("override", None)
        return stub

    def test_oversize_is_held_never_scored(self):
        stub = self._stub()
        env = enabled_env(stub)
        record = unit(answer="x" * 100000)
        payload = gd.score([record], ledger=fresh_ledger(), env=env)
        r = payload["records"][0]
        self.assertEqual(r["outcome"], "oversize")
        self.assertEqual(r["scores"], {})
        action, detail = gd.classify(r, "hold", 3)
        self.assertEqual(action, gd.HELD)
        self.assertTrue(detail, detail)

    def test_unreachable_model_is_not_a_low_score(self):
        env = enabled_env(h.Stub())  # base_url overridden below
        env["CONTEXT_MEMORY_DECISIONS_BASE_URL"] = "http://127.0.0.1:1"  # not listening
        payload = gd.score([unit()], ledger=fresh_ledger(), env=env)
        r = payload["records"][0]
        self.assertNotEqual(r["outcome"], "scored")
        action, detail = gd.classify(r, "hold", 3)
        self.assertEqual(action, gd.WRITE)
        self.assertTrue(detail.startswith("gate skipped"), detail)

    def test_malformed_response_is_not_a_score(self):
        stub = self._stub(override="malformed")
        payload = gd.score([unit()], ledger=fresh_ledger(), env=enabled_env(stub))
        r = payload["records"][0]
        self.assertNotEqual(r["outcome"], "scored")
        self.assertEqual(gd.classify(r, "hold", 3)[0], gd.WRITE)

    def test_hard_config_refusal_raises(self):
        # A non-loopback http URL without an API key is refused by the gate (exit 1). `score` must
        # surface the refusal as a `GateRefused`, not collapse to an empty batch and let the export
        # write every unit ungated (issue 190).
        env = h.gate_subprocess_env({
            "CONTEXT_MEMORY_DECISIONS_ENABLED": "true",
            "CONTEXT_MEMORY_DECISIONS_BASE_URL": "http://example.com",
        })
        with self.assertRaises(gd.GateRefused) as raised:
            gd.score([unit()], ledger=fresh_ledger(), env=env)
        self.assertEqual(raised.exception.outcome, "bad-decisions-url")

    def test_planted_secret_never_reaches_the_stub(self):
        secret = "sk-abc123def456ghi789jkl0mno1pqr2suv3"  # an API-key shape the redactor scrubs
        stub = self._stub()
        record = unit(answer=f"connect using {secret}", description="secret use")
        gd.score([record], ledger=fresh_ledger(), env=enabled_env(stub))
        for req in stub.requests:
            self.assertNotIn(secret, json.dumps(req),
                             "the redacted secret reached the model request")
            self.assertNotIn("abc123def456ghi789jkl0mno1pqr2suv3", json.dumps(req),
                             "the token part of the secret reached the model request")


class DedupIgnoredTests(unittest.TestCase):
    def test_dedup_keys_set_do_not_change_behaviour(self):
        stub = h.Stub()
        self.addCleanup(stub.close)
        stub.probabilities = {"developer": 0.9, "tester": 0.1}
        env_plain = enabled_env(stub)
        env_dedup = enabled_env(stub, **DEDUP_KEYS)
        r_plain = gd.score([unit()], ledger=fresh_ledger(), env=env_plain)["records"][0]
        r_dedup = gd.score([unit()], ledger=fresh_ledger(), env=env_dedup)["records"][0]
        self.assertEqual(r_plain["passed"], r_dedup["passed"])
        self.assertEqual(r_plain["outcome"], r_dedup["outcome"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
