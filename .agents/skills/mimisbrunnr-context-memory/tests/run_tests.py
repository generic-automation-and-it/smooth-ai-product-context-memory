#!/usr/bin/env python3
"""L0 committed harness for the mimisbrunnr-context-memory skill plumbing.

Standard-library unittest only — no external test runner dependency. Exercises the deterministic
artefacts (redact.py, atomicity.py) over explicit positive and negative fixtures, asserting real
behaviour rather than re-deriving the rules (the anti-pattern run-trial.js fell into).

Run: python3 tests/run_tests.py
"""

import importlib.util
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace

HERE = Path(__file__).resolve().parent
SCRIPTS = HERE.parent / "scripts"


def _load(name):
    spec = importlib.util.spec_from_file_location(name, SCRIPTS / f"{name}.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


redact = _load("redact")
atomicity = _load("atomicity")
client = _load("context_memory_client")


def _run_atomicity(batch):
    """Drive atomicity.py end to end, so the description/statement split is covered, not just classify()."""
    completed = subprocess.run(
        [sys.executable, str(SCRIPTS / "atomicity.py")],
        input=json.dumps(batch),
        capture_output=True,
        text=True,
        check=True,
    )
    return json.loads(completed.stdout)


def _scrub_item(content):
    redacted, findings = redact._scrub(content)
    return {
        "redacted": redacted,
        "findings": [{"rule_name": name, "hit_count": count} for name, count in sorted(findings.items())],
    }


class RedactTests(unittest.TestCase):
    def test_planted_aws_key_never_leaks(self):
        item = "The deployment uses AKIAIOSFODNN7EXAMPLE for CI."
        result = _scrub_item(item)
        self.assertNotIn("AKIAIOSFODNN7EXAMPLE", result["redacted"])
        self.assertTrue(any(f["rule_name"] == "aws-access-key-id" for f in result["findings"]))

    def test_planted_github_token_never_leaks(self):
        item = "The runner uses ghp_1234567890abcdefghijklmnopqrstuvwxyz for push."
        result = _scrub_item(item)
        self.assertNotIn("ghp_", result["redacted"])
        self.assertTrue(any(f["rule_name"] == "github-token" for f in result["findings"]))

    def test_connection_string_password_scrubbed(self):
        item = 'Server=db;Port=5432;User Id=sa;Password=Sup3rS3cret!;Database=app'
        result = _scrub_item(item)
        self.assertNotIn("Sup3rS3cret!", result["redacted"])
        self.assertTrue(any(f["rule_name"] == "connection-string-password" for f in result["findings"]))

    def test_private_key_pem_scrubbed(self):
        item = "-----BEGIN RSA PRIVATE KEY-----\nMIICXg==\n-----END RSA PRIVATE KEY-----"
        result = _scrub_item(item)
        self.assertNotIn("MIICXg==", result["redacted"])
        self.assertTrue(any(f["rule_name"] == "private-key-pem" for f in result["findings"]))

    def test_finding_reports_rule_name_only(self):
        item = "token: abcdef1234567890"
        result = _scrub_item(item)
        for f in result["findings"]:
            self.assertIn("rule_name", f)
            # The matched span must never appear in the finding.
            self.assertNotIn("abcdef1234567890", json.dumps(f))

    def test_clean_content_has_no_findings(self):
        item = "PostgreSQL stores our search index."
        result = _scrub_item(item)
        self.assertEqual(result["redacted"], item)
        self.assertEqual(result["findings"], [])

    def test_redact_and_flag_never_rejects(self):
        # A leak is scrubbed and recorded, not treated as a reason to drop the fact.
        item = "The deployment uses DEPLOY_TOKEN=abcdef1234567890 for auth."
        result = _scrub_item(item)
        self.assertIsNotNone(result["redacted"])
        self.assertNotIn("abcdef1234567890", result["redacted"])
        self.assertNotIn("abcdef1234567890", json.dumps(result["findings"]))
        self.assertTrue(any(f["rule_name"] == "generic-secret-assignment" for f in result["findings"]))


class AtomicityTests(unittest.TestCase):
    def test_single_atomic_fact_is_simple(self):
        verdict = atomicity.classify("PostgreSQL stores our search index.")
        self.assertEqual(verdict["verdict"], "simple")

    def test_bundled_tally_is_flagged(self):
        verdict = atomicity.classify("Three things matter: the API, the storage, and the model.")
        self.assertEqual(verdict["verdict"], "bundled")

    def test_bundled_compound_is_flagged(self):
        verdict = atomicity.classify(
            "We chose PostgreSQL, and it also explains why we need JSONB, but it further means the index is GIN."
        )
        self.assertEqual(verdict["verdict"], "bundled")

    def test_clean_single_claim_with_conjunction_stays_simple(self):
        # One "and" between attributes is not a bundle signal.
        verdict = atomicity.classify("The service is local-first and private.")
        self.assertEqual(verdict["verdict"], "simple")

    def test_empty_input_is_simple(self):
        verdict = atomicity.classify("")
        self.assertEqual(verdict["verdict"], "simple")

    def test_reason_clause_is_one_fact(self):
        # A fact plus the reason it holds is one fact. Scoring "because" as a claim junction made
        # this a false positive on a real braindump batch.
        verdict = atomicity.classify(
            "PostgreSQL is selected because typed indexed relations and JSONB support server-side "
            "querying of rich evolving metadata."
        )
        self.assertEqual(verdict["verdict"], "simple")
        self.assertEqual(verdict["signals"], [])

    def test_coordinated_pair_is_one_fact(self):
        verdict = atomicity.classify(
            "The memory service is a local HTTP Docker API consumed manually through an AI harness "
            "skill for both capture and retrieval."
        )
        self.assertEqual(verdict["verdict"], "simple")

    def test_semicolon_between_clauses_is_bundled(self):
        verdict = atomicity.classify(
            "One memory represents one atomic fact; retrieval returns arrays of statements."
        )
        self.assertEqual(verdict["verdict"], "bundled")

    def test_single_contrastive_junction_is_bundled(self):
        verdict = atomicity.classify(
            "Groups provide stable episodic umbrellas while append-only versions preserve changed "
            "child-memory claims."
        )
        self.assertEqual(verdict["verdict"], "bundled")

    def test_noun_phrase_list_alone_is_not_bundled(self):
        verdict = atomicity.classify("The store keeps facets, tags, and scope dimensions.")
        self.assertEqual(verdict["verdict"], "simple")

    def test_signal_set_discriminates_between_verdicts(self):
        # The regression this guards: every candidate of a real batch carried the same signal, so
        # the signal list said nothing about the verdict.
        simple = atomicity.classify("Capture derives a content summary so retrieval stays compact.")
        bundled = atomicity.classify("Facets filter dependably while free tags stay expressive.")
        self.assertEqual(simple["signals"], [])
        self.assertIn("discourse", bundled["signals"])

    def test_verdict_scores_the_statement_not_the_description(self):
        # A description is a subject label; coordination inside it ("labels and tags") is not a
        # second claim and must not push the candidate to bundled.
        batch = [{"description": "Controlled labels and free tags", "statement": "Facets filter."}]
        result = _run_atomicity(batch)
        self.assertEqual(result["results"][0]["verdict"], "simple")

    def test_description_is_scored_when_no_statement_is_supplied(self):
        batch = [{"description": "Facets filter dependably while free tags stay expressive."}]
        result = _run_atomicity(batch)
        self.assertEqual(result["results"][0]["verdict"], "bundled")


class PathsClientTests(unittest.TestCase):
    FIXTURES = HERE / "fixtures"

    def _args(self, payload_path):
        return SimpleNamespace(payload=str(payload_path))

    def test_plain_and_filtered_request_shapes_carry_required_fields(self):
        for name in ("paths_plain.json", "paths_filtered.json"):
            with self.subTest(name=name):
                request = json.loads((self.FIXTURES / name).read_text(encoding="utf-8"))["request"]
                self.assertIn("sourceUuid", request)
                self.assertIn("maxDepth", request)

    def test_filtered_request_reaches_all_filters(self):
        request = json.loads((self.FIXTURES / "paths_filtered.json").read_text(encoding="utf-8"))["request"]
        for field in ("sourceUuid", "maxDepth", "targetUuid", "relation", "direction", "kind", "status", "scopeDimension", "limit"):
            self.assertIn(field, request)

    def test_render_path_matches_expected_summary(self):
        data = json.loads((self.FIXTURES / "paths_plain.json").read_text(encoding="utf-8"))
        path = data["response"]["paths"][0]
        self.assertEqual(client._render_path(path), data["expected_summary"])

    def test_missing_max_depth_is_rejected_before_any_network_call(self):
        for bad in ({"sourceUuid": "aaaaaaaa-0000-0000-0000-000000000000"},
                    {"sourceUuid": "aaaaaaaa-0000-0000-0000-000000000000", "maxDepth": None},
                    {"sourceUuid": "aaaaaaaa-0000-0000-0000-000000000000", "maxDepth": 0},
                    {"sourceUuid": "aaaaaaaa-0000-0000-0000-000000000000", "maxDepth": "2"},
                    {"sourceUuid": "aaaaaaaa-0000-0000-0000-000000000000", "maxDepth": True}):
            with self.subTest(payload=bad):
                payload_path = self._write_temp(bad)
                try:
                    with self.assertRaises(client.ClientError) as ctx:
                        client.cmd_paths(self._args(payload_path))
                    self.assertIn("maxDepth", str(ctx.exception))
                finally:
                    os.unlink(payload_path)

    def test_missing_source_uuid_is_rejected_before_any_network_call(self):
        for bad in ({"maxDepth": 2}, {"maxDepth": 2, "sourceUuid": None}, {"maxDepth": 2, "sourceUuid": "  "}):
            with self.subTest(payload=bad):
                payload_path = self._write_temp(bad)
                try:
                    with self.assertRaises(client.ClientError) as ctx:
                        client.cmd_paths(self._args(payload_path))
                    self.assertIn("sourceUuid", str(ctx.exception))
                finally:
                    os.unlink(payload_path)

    def test_unknown_source_uuid_error_shape_is_documented(self):
        data = json.loads((self.FIXTURES / "paths_error_unknown_source.json").read_text(encoding="utf-8"))
        self.assertEqual(data["http_status"], 404)
        self.assertEqual(data["client_exit_code"], 1)
        self.assertTrue(data["client_error_prefix"].startswith("HTTP 404"))

    @staticmethod
    def _write_temp(obj):
        handle = tempfile.NamedTemporaryFile("w", suffix=".json", delete=False, encoding="utf-8")
        with handle:
            json.dump(obj, handle)
        return handle.name


if __name__ == "__main__":
    unittest.main(verbosity=2)
