#!/usr/bin/env python3
"""Committed L0 harness for the mimisbrunnr-muninn-recall-feedback request guard.

Run: python3 -B .agents/skills/mimisbrunnr-muninn-recall-feedback/tests/run_tests.py

Sources scripts/recall_feedback.sh in bash with a fake `curl` (and a recording `python3` shim) first on
PATH, so no request leaves the machine. Covers:
  - a non-loopback origin and a userinfo origin are refused before any request, and the refusal does
    not echo the credential;
  - a path that would move the host (`@host/...`, `//host/...`, a scheme, whitespace) is refused before
    any request;
  - an accepted request carries `--noproxy '*'`, sends the token from a header file and never on argv,
    and unlinks that file afterwards — including when the send is interrupted;
  - the base URL never reaches any process's argv;
  - SKILL.md sources this script rather than carrying its own copy of the guard.

stdlib unittest; no external runner. bash 3.2 and GNU bash.
"""

import os
import shutil
import stat
import subprocess
import sys
import tempfile
import textwrap
import unittest
from pathlib import Path

SKILL = Path(__file__).resolve().parents[1]
SCRIPT = SKILL / "scripts" / "recall_feedback.sh"
TOKEN = "synthetic-recall-feedback-token-0123456789"
READ_PATH = "/api/context/recall-feedback/miss-rate?from=2026-09-11&to=2026-09-18"

FAKE_CURL = """\
#!/usr/bin/env bash
# Records argv one element per line, and the header file's contents and mode at call time.
log="$RF_TEST_DIR/curl.argv"
: >"$log"
for arg in "$@"; do printf '%s\\n' "$arg" >>"$log"; done
prev=""
for arg in "$@"; do
  if [ "$prev" = "-H" ]; then
    case "$arg" in
      @*) cp "${arg#@}" "$RF_TEST_DIR/curl.header"; ls -l "${arg#@}" | cut -c1-10 >"$RF_TEST_DIR/curl.mode"
          printf '%s\\n' "${arg#@}" >"$RF_TEST_DIR/curl.headerpath" ;;
    esac
  fi
  prev="$arg"
done
if [ -n "${RF_TEST_KILL_PARENT:-}" ]; then kill -TERM "$PPID"; fi
echo '{"retrievals":0,"misses":0,"missRate":0.0}'
exit "${RF_TEST_CURL_STATUS:-0}"
"""

# Records every python3 argv so the test can prove the base URL is not one of them, then runs the
# real interpreter.
PYTHON_SHIM = """\
#!/usr/bin/env bash
for arg in "$@"; do printf '%s\\n' "$arg" >>"$RF_TEST_DIR/python.argv"; done
exec "$RF_REAL_PYTHON" "$@"
"""


class RecallFeedbackGuardTests(unittest.TestCase):
    def setUp(self):
        self.dir = Path(tempfile.mkdtemp(prefix="rf-guard-"))
        self.bin = self.dir / "bin"
        self.bin.mkdir()
        self.tmp = self.dir / "tmp"
        self.tmp.mkdir()
        for name, body in (("curl", FAKE_CURL), ("python3", PYTHON_SHIM)):
            path = self.bin / name
            path.write_text(body, encoding="utf-8")
            path.chmod(path.stat().st_mode | stat.S_IXUSR)

    def tearDown(self):
        shutil.rmtree(self.dir, ignore_errors=True)

    def run_curl(self, path=READ_PATH, token=TOKEN, base=None, extra_env=None, method="GET"):
        env = {
            "PATH": "{}:{}".format(self.bin, os.environ.get("PATH", "")),
            "HOME": str(self.dir),
            "TMPDIR": str(self.tmp),
            "RF_TEST_DIR": str(self.dir),
            "RF_REAL_PYTHON": sys.executable,
        }
        if base is not None:
            env["CONTEXT_MEMORY_BASE_URL"] = base
        env.update(extra_env or {})
        script = textwrap.dedent("""\
            source "$1"
            recall_feedback_curl "$2" "$3" "$4"
            """)
        return subprocess.run(
            ["bash", "-c", script, "harness", str(SCRIPT), method, path, token],
            env=env, capture_output=True, text=True, timeout=30)

    def curl_called(self):
        return (self.dir / "curl.argv").exists()

    def curl_argv(self):
        return (self.dir / "curl.argv").read_text(encoding="utf-8").splitlines()

    def assert_refused(self, result):
        self.assertNotEqual(result.returncode, 0, result.stderr)
        self.assertIn("no request was sent", result.stderr)
        self.assertFalse(self.curl_called(), "curl ran after a refusal")
        self.assertEqual(list(self.tmp.iterdir()), [], "a header file was written after a refusal")

    # --- origin ---------------------------------------------------------------------------------

    def test_non_loopback_origin_is_refused(self):
        result = self.run_curl(base="https://api.example.com")
        self.assert_refused(result)
        self.assertIn("non-loopback", result.stderr)

    def test_loopback_prefix_with_non_loopback_host_is_refused(self):
        self.assert_refused(self.run_curl(base="http://localhost:5141@192.0.2.1/"))

    def test_userinfo_origin_is_refused_without_echoing_it(self):
        result = self.run_curl(base="http://operator:hunter2-secret@localhost:5141")
        self.assert_refused(result)
        self.assertIn("userinfo=present", result.stderr)
        self.assertNotIn("hunter2-secret", result.stderr + result.stdout)

    def test_path_bearing_origin_is_refused(self):
        self.assert_refused(self.run_curl(base="http://localhost:5141/api"))

    # --- path -----------------------------------------------------------------------------------

    def test_path_that_moves_the_host_is_refused(self):
        for path in ("@evil.example/x", "/x@evil.example", "//evil.example/x",
                     "http://evil.example/x", "evil/x", "", "/a b", "/a\\b", "/a\tb"):
            with self.subTest(path=path):
                self.assert_refused(self.run_curl(path=path))

    # --- token ----------------------------------------------------------------------------------

    def test_missing_token_is_refused(self):
        self.assert_refused(self.run_curl(token=""))

    def test_accepted_request_bypasses_proxy_and_keeps_token_off_argv(self):
        result = self.run_curl(extra_env={"http_proxy": "http://proxy.invalid:3128",
                                          "ALL_PROXY": "http://proxy.invalid:3128"})
        self.assertEqual(result.returncode, 0, result.stderr)
        argv = self.curl_argv()
        index = argv.index("--noproxy")
        self.assertEqual(argv[index + 1], "*")
        self.assertEqual(argv[-1], "http://localhost:5141" + READ_PATH)
        self.assertFalse(any(TOKEN in arg for arg in argv), "token reached curl argv")
        header = (self.dir / "curl.header").read_text(encoding="utf-8")
        self.assertEqual(header, "Authorization: Bearer {}\n".format(TOKEN))
        self.assertEqual((self.dir / "curl.mode").read_text(encoding="utf-8").strip(), "-rw-------")
        header_path = Path((self.dir / "curl.headerpath").read_text(encoding="utf-8").strip())
        self.assertFalse(header_path.exists(), "header file outlived the request")

    def test_trailing_slash_origin_does_not_double_the_separator(self):
        result = self.run_curl(base="http://127.0.0.1:5141/")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.curl_argv()[-1], "http://127.0.0.1:5141" + READ_PATH)

    def test_curl_failure_status_is_returned_and_header_removed(self):
        result = self.run_curl(extra_env={"RF_TEST_CURL_STATUS": "22"})
        self.assertEqual(result.returncode, 22, result.stderr)
        self.assertEqual(list(self.tmp.iterdir()), [])

    def test_interrupted_send_removes_header_file(self):
        result = self.run_curl(extra_env={"RF_TEST_KILL_PARENT": "1"})
        self.assertTrue(self.curl_called())
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(list(self.tmp.iterdir()), [], "header file survived an interrupt")

    def test_base_url_never_reaches_python_argv(self):
        base = "http://operator:argv-secret@localhost:5141"
        self.run_curl(base=base)
        recorded = (self.dir / "python.argv").read_text(encoding="utf-8")
        self.assertNotIn("argv-secret", recorded)
        self.run_curl(base="http://localhost:5141")
        recorded = (self.dir / "python.argv").read_text(encoding="utf-8")
        self.assertNotIn("localhost:5141", recorded)

    # --- documentation stays wired to the tested code ---------------------------------------------

    def test_skill_sources_the_tested_script(self):
        skill = (SKILL / "SKILL.md").read_text(encoding="utf-8")
        self.assertIn("scripts/recall_feedback.sh", skill)
        self.assertNotIn("recall_feedback_guard() {", skill,
                         "SKILL.md carries its own copy of the guard instead of sourcing the script")


if __name__ == "__main__":
    unittest.main(verbosity=2)
