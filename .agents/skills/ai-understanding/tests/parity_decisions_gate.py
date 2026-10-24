#!/usr/bin/env python3
"""WET parity harness for the ai-understanding decision gate.

The gate is a deliberate copy (WET) of the Mímisbrunnr gate, and the price of the copy is drift. This
harness compares the copy with the original at every surface that must not diverge — rubric bytes,
defaults and env names, the request JSON for a fixture unit, classified outcomes against the same stub,
redaction output on the shared secret-shape corpus, and the default-threshold pin against the documented
value. A drift fails with a message naming the side that changed.

Run: python3 -B .agents/skills/ai-understanding/tests/parity_decisions_gate.py

Not shipped to the template; it lives in the repository that owns the copy.
"""

from __future__ import annotations

import json
import shutil
import tempfile
import unittest
from pathlib import Path

import _gate_harness as h

COPY = h._decisions_gate
ORIG = h._orig_gate

ENV_NAMES = {
    "ENABLED", "BASE_URL", "PATH", "MODEL", "API_KEY", "MIN_PROBABILITY",
    "MAX_ATTEMPTS", "ROLES", "BELOW_THRESHOLD", "TIMEOUT", "LEDGER_MAX_ENTRIES",
}
DEFAULTS = {
    "DEFAULT_BASE_URL", "DEFAULT_PATH", "DEFAULT_MODEL", "DEFAULT_MIN_PROBABILITY",
    "DEFAULT_MAX_ATTEMPTS", "DEFAULT_TIMEOUT", "DEFAULT_ROLES", "DEFAULT_BELOW_THRESHOLD",
    "MAX_CONTEXT_TOKENS", "MAX_BODY_BYTES", "MAX_QUESTIONS", "MAX_LEDGER_ENTRIES",
    "CHARS_PER_TOKEN", "REDACTOR_TIMEOUT_SECONDS",
}

RECORD = {
    "subject": "Storage engine decision",
    "statement": "PostgreSQL is the storage engine, chosen for AGE support.",
    "summary": "AGE adjacency decided it.",
    "boundaries": "not on a managed cloud",
    "kind": "architecture",
    "scope": "portable",
}
SECRET_CORPUS = [
    {"subject": "S", "statement": "connect with sk-abc123def456ghi789jkl0mno1pqr2suv3",
     "summary": "", "boundaries": "", "kind": "", "scope": ""},
    {"subject": "S2", "statement": "password=hunter2secretvalue; host=db.internal",
     "summary": "", "boundaries": "", "kind": "", "scope": ""},
]


def env_name_set(module) -> set[str]:
    return {
        getattr(module, name) for name in dir(module)
        if name.startswith("ENV_")
        and isinstance(getattr(module, name), str)
        and getattr(module, name).startswith("CONTEXT_MEMORY_DECISIONS_")
    }


def constant(module, name):
    return getattr(module, name)


@unittest.skipUnless(ORIG is not None,
                     "the Mímisbrunnr gate is not present here; the parity harness is repo-local")
class ParityTests(unittest.TestCase):
    def test_rubric_bytes_identical(self):
        self.assertEqual(
            h.RUBRIC.read_bytes(), h.ORIG_RUBRIC.read_bytes(),
            "ai-understanding decisions_rubric.json differs from the Mímisbrunnr rubric; "
            "re-copy it, or record the divergence deliberately")

    def test_defaults_identical(self):
        for name in DEFAULTS:
            self.assertEqual(constant(COPY, name), constant(ORIG, name),
                             f"copy {name} ({constant(COPY, name)!r}) differs from the original "
                             f"({constant(ORIG, name)!r})")

    def test_env_name_set_matches_the_original(self):
        self.assertEqual(env_name_set(COPY), env_name_set(ORIG))
        self.assertEqual(env_name_set(COPY), {f"CONTEXT_MEMORY_DECISIONS_{n}" for n in ENV_NAMES})

    def test_no_dedup_name_in_the_copy(self):
        for name in env_name_set(COPY):
            self.assertNotIn("DEDUP", name,
                             "the copy must not read a DEDUP_* key — dedup is out of scope "
                             "(it is Mímisbrunnr's, not this skill's)")

    def test_default_threshold_is_085_in_both(self):
        self.assertEqual(constant(COPY, "DEFAULT_MIN_PROBABILITY"), 0.85)
        self.assertEqual(constant(ORIG, "DEFAULT_MIN_PROBABILITY"), 0.85)

    def test_code_default_pinned_to_documented_085(self):
        run_sh = h.REPO / "scripts/run.sh"
        setup = h.REPO / "docs/wiki/setup.md"
        run_text = run_sh.read_text(encoding="utf-8")
        setup_text = setup.read_text(encoding="utf-8")
        self.assertIn("CONTEXT_MEMORY_DECISIONS_MIN_PROBABILITY=0.85", run_text,
                      "run.sh must publish the code default 0.85")
        self.assertIn("CONTEXT_MEMORY_DECISIONS_MIN_PROBABILITY", setup_text)
        self.assertIn("0.85", setup_text.split("CONTEXT_MEMORY_DECISIONS_MIN_PROBABILITY")[1][:400],
                      "setup.md must document the threshold as 0.85")

    def test_request_json_identical_for_a_fixture_unit(self):
        roles = COPY.load_rubric(["product-owner", "designer", "developer", "tester", "business"])[1]
        state = COPY.record_state(RECORD)
        req_copy = COPY.build_request(state, roles, "nimble")
        req_orig = ORIG.build_request(state, roles, "nimble")
        self.assertEqual(req_copy, req_orig,
                         "the copy builds a different decision request for the same unit")

    def test_redaction_identical_on_the_secret_corpus(self):
        scrubbed_copy, _find_copy = COPY.redact_records(SECRET_CORPUS)
        scrubbed_orig, _find_orig = ORIG.redact_records(SECRET_CORPUS)
        self.assertEqual(scrubbed_copy, scrubbed_orig,
                         "the copy redacts differently on the shared secret-shape corpus")
        self.assertNotIn("sk-abc123def456ghi789jkl0mno1pqr2suv3", json.dumps(scrubbed_copy))
        self.assertNotIn("hunter2secretvalue", json.dumps(scrubbed_copy))

    def test_classified_outcomes_identical_against_the_same_stub(self):
        stub = h.Stub()
        self.addCleanup(stub.close)
        stub.probabilities = {"developer": 0.9, "tester": 0.1, "product-owner": 0.2}
        env = {"CONTEXT_MEMORY_DECISIONS_ENABLED": "true",
               "CONTEXT_MEMORY_DECISIONS_BASE_URL": stub.base_url,
               "CONTEXT_MEMORY_DECISIONS_MODEL": "nimble"}
        records = [RECORD]
        copy_out = json.loads(h.run_gate(["score"], json.dumps(records), env, gate=h.GATE).stdout)
        orig_out = json.loads(h.run_gate(["score"], json.dumps(records), env, gate=h.ORIG_GATE).stdout)
        self.assertEqual(copy_out["records"], orig_out["records"],
                         "the copy classifies a fixture unit differently from the original")
        self.assertEqual(copy_out["records"][0]["outcome"], "scored")
        self.assertTrue(copy_out["records"][0]["passed"])
        self.assertEqual(copy_out["rubricVersion"], orig_out["rubricVersion"])

    def test_valid_best_and_finite_number_pure_functions_agree(self):
        # The post-draft review fixes (#196/#204) live in `_finite_number`/`valid_best`; the copy must
        # agree with the original on the edge shapes that bit them.
        for value in (0.0, 0.5, 1.0, "x", None, [], {}):
            self.assertEqual(COPY._finite_number(value), ORIG._finite_number(value))
        for best in ({"max": 0.9, "scores": {"a": 0.9}}, {"max": float("nan"), "scores": {}},
                     {"max": "x", "scores": {}}, None, {}):
            self.assertEqual(COPY.valid_best(best), ORIG.valid_best(best))

    def test_empty_override_falls_back_identically(self):
        # An explicitly-empty numeric config falls back to the shipped default in both gates (#194);
        # `number()` returns the fallback on an empty value. (An empty `BELOW_THRESHOLD` is not this
        # case — the enum refuses an empty string.)
        stub = h.Stub()
        self.addCleanup(stub.close)
        stub.probabilities = {"developer": 0.2, "tester": 0.2}
        env = {"CONTEXT_MEMORY_DECISIONS_ENABLED": "true",
               "CONTEXT_MEMORY_DECISIONS_BASE_URL": stub.base_url,
               "CONTEXT_MEMORY_DECISIONS_MODEL": "nimble",
               "CONTEXT_MEMORY_DECISIONS_MIN_PROBABILITY": ""}
        records = [RECORD]
        copy_out = json.loads(h.run_gate(["score"], json.dumps(records), env, gate=h.GATE).stdout)
        orig_out = json.loads(h.run_gate(["score"], json.dumps(records), env, gate=h.ORIG_GATE).stdout)
        self.assertEqual(copy_out["minProbability"], 0.85)
        self.assertEqual(copy_out["minProbability"], orig_out["minProbability"])
        self.assertEqual(copy_out["records"], orig_out["records"])

    def test_a_one_char_rubric_change_fails_the_byte_check(self):
        # Prove the comparison is sensitive, not a tautology: a single-character change to the rubric
        # bytes makes the equality check fail. The real drift is caught the same way.
        root = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, root, True)
        mutated = root / "decisions_rubric.json"
        bytes_in = h.RUBRIC.read_bytes()
        mutated.write_bytes(bytes_in + b" ")
        self.assertNotEqual(mutated.read_bytes(), h.ORIG_RUBRIC.read_bytes())


if __name__ == "__main__":
    unittest.main(verbosity=2)
