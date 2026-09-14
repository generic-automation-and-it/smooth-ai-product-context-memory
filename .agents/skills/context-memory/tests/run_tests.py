#!/usr/bin/env python3
"""L0 committed harness for the context-memory skill plumbing.

Standard-library unittest only — no external test runner dependency. Exercises the deterministic
artefacts (redact.py, atomicity.py) over explicit positive and negative fixtures, asserting real
behaviour rather than re-deriving the rules (the anti-pattern run-trial.js fell into).

Run: python3 tests/run_tests.py
"""

import contextlib
import importlib.util
import io
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPTS = HERE.parent / "scripts"


def _load(name):
    spec = importlib.util.spec_from_file_location(name, SCRIPTS / f"{name}.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


redact = _load("redact")
atomicity = _load("atomicity")


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


class PathsTests(unittest.TestCase):
    """Server-independent assertions over the `paths` subcommand's deterministic parts.

    The HTTP round-trip needs a live store, so it stays a manual check (per the worktask); what the
    client guarantees without a server is the request guard and the legible rendering.
    """

    def setUp(self):
        self.client = _load("context_memory_client")

    def test_paths_requires_an_explicit_maxdepth(self):
        with tempfile.NamedTemporaryFile("w", suffix=".json", delete=False) as fh:
            json.dump({"sourceUuid": "aaaaaaaa-1111-1111-1111-111111111111"}, fh)
            path = fh.name
        try:
            import argparse

            args = argparse.Namespace(payload=path)
            with self.assertRaises(self.client.ClientError) as ctx:
                self.client.cmd_paths(args)
            self.assertIn("maxDepth", str(ctx.exception))
        finally:
            os.unlink(path)

    def test_render_prefers_names_and_relations_over_uuids(self):
        resp = {
            "paths": [
                {
                    "depth": 2,
                    "hops": [
                        {
                            "sourceUuid": "aaaaaaaa-1111-1111-1111-111111111111",
                            "targetUuid": "bbbbbbbb-2222-2222-2222-222222222222",
                            "relation": "depends_on",
                            "reason": "the finding justified the decision",
                        }
                    ],
                    "endpoint": {
                        "uuid": "bbbbbbbb-2222-2222-2222-222222222222",
                        "name": "Adopt the cache",
                        "statement": "Adopt the cache",
                    },
                }
            ]
        }
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            self.client._render_paths(resp)

        out = buf.getvalue()
        self.assertIn("Adopt the cache", out)
        self.assertIn("depends_on", out)
        # The terminal endpoint is labelled by its name, not its bare uuid.
        self.assertNotIn("bbbbbbbb-2222-2222-2222-222222222222", out)

    def test_render_no_paths_is_honest(self):
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            self.client._render_paths({"paths": []})
        self.assertEqual(buf.getvalue().strip(), "No paths found.")


if __name__ == "__main__":
    unittest.main(verbosity=2)
