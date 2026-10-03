import importlib.util
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("crash_acceptance", Path(__file__).with_name("crash_acceptance.py"))
crash = importlib.util.module_from_spec(spec)
spec.loader.exec_module(crash)


class CrashHarnessGuards(unittest.TestCase):
    def test_requires_explicit_disruption_flag(self):
        with self.assertRaisesRegex(crash.AcceptanceError, "explicit_test_disruption_flag_required"):
            crash.Harness({}, "unused.json")

    def test_credentials_ignore_inherited_endpoints_and_tokens(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "protected.env"
            path.write_text("CONTEXT_MEMORY_READ_TOKEN=read-test\nCONTEXT_MEMORY_WRITE_TOKEN=write-test\n"
                "MIMI_KNOWLEDGE_READ_TOKEN=service-read-test\nMIMI_KNOWLEDGE_WRITE_TOKEN=service-write-test\n"
                "CONTEXT_MEMORY_BASE_URL=http://example.invalid\nMIMI_KNOWLEDGE_URL=http://example.invalid\n", encoding="utf-8")
            if os.name != "nt":
                path.chmod(0o600)
            with patch.dict(os.environ, {"CONTEXT_MEMORY_READ_TOKEN": "wrong", "MIMI_KNOWLEDGE_URL": "http://wrong.invalid"}):
                values = crash.credentials(path)
            self.assertEqual(values["CONTEXT_MEMORY_READ_TOKEN"], "read-test")
            self.assertEqual(len(values), 4)
            self.assertEqual(crash.CORE_URL, "http://127.0.0.1:15141")
            self.assertEqual(crash.SERVICE_URL, "http://127.0.0.1:15142")

    def test_missing_credentials_error_contains_no_values(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "protected.env"
            path.write_text("CONTEXT_MEMORY_READ_TOKEN=secret-example-value\n", encoding="utf-8")
            if os.name != "nt":
                path.chmod(0o600)
            with self.assertRaisesRegex(crash.AcceptanceError, "^missing_test_credentials$"):
                crash.credentials(path)

    def test_fingerprint_detects_extra_version_or_source(self):
        rows = [{"uuid": "synthetic", "version": 1, "sources": []}]
        self.assertEqual(crash.fingerprint(rows), crash.fingerprint([{"sources": [], "version": 1, "uuid": "synthetic"}]))
        self.assertNotEqual(crash.fingerprint(rows), crash.fingerprint(rows + [{"uuid": "synthetic", "version": 2, "sources": []}]))
        self.assertNotEqual(crash.fingerprint(rows), crash.fingerprint([{"uuid": "synthetic", "version": 1, "sources": [{"reference": "duplicate"}]}]))


if __name__ == "__main__":
    unittest.main()
