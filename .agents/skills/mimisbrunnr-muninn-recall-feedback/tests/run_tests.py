#!/usr/bin/env python3
"""Committed L0 harness for the mimisbrunnr-muninn-recall-feedback request guard.

Run: python3 -B .agents/skills/mimisbrunnr-muninn-recall-feedback/tests/run_tests.py

Sources scripts/recall_feedback.sh in bash with a fake `curl` (and a recording `python3` shim) first on
PATH, so no request leaves the machine. Covers:
  - a non-loopback origin and a userinfo origin are refused before any request, and the refusal does
    not echo the credential;
  - a path that would move the host (`@host/...`, `//host/...`, a scheme, whitespace) is refused before
    any request;
  - every curl call opens with `-q`, so a default `.curlrc` cannot alter the request (the fake curl
    refuses any call that does not, and a real-curl case proves a `.curlrc` canary is ignored);
  - an accepted request carries `--noproxy '*'`, sends the token from a header file and never on argv,
    and unlinks that file afterwards — including when the send is interrupted;
  - a `403` is a non-zero exit with its body kept: the option reaches the fake curl, and a real curl
    against a loopback responder proves the behaviour (skipped when curl lacks `--fail-with-body`);
  - the base URL never reaches any process's argv;
  - SKILL.md sources this script rather than carrying its own copy of the guard, and its documented
    queries take their window from operator-set variables instead of a fixed date.

stdlib unittest; no external runner. bash 3.2 and GNU bash.
"""

import http.server
import os
import re
import shutil
import stat
import subprocess
import sys
import tempfile
import textwrap
import threading
import unittest
from pathlib import Path

SKILL = Path(__file__).resolve().parents[1]
SCRIPT = SKILL / "scripts" / "recall_feedback.sh"
TOKEN = "synthetic-recall-feedback-token-0123456789"
READ_PATH = "/api/context/recall-feedback/miss-rate?from=2026-09-11&to=2026-09-18"


def _curl_has_fail_with_body():
    if not shutil.which("curl"):
        return False
    probe = subprocess.run(["curl", "--help", "all"], capture_output=True, text=True, timeout=30)
    return "--fail-with-body" in probe.stdout


CURL_HAS_FAIL_WITH_BODY = _curl_has_fail_with_body()

FAKE_CURL = """\
#!/usr/bin/env bash
# Records argv one element per line, and the header file's contents and mode at call time.
# Refuses any call whose first argument is not -q: without it a real curl reads ~/.curlrc first, so
# every test that reaches curl also proves the default config is disabled.
if [ "${1:-}" != "-q" ]; then echo "fake curl: -q is not argv[1]" >&2; exit 97; fi
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

    def test_path_query_and_fragment_are_reported_as_present_not_echoed(self):
        # A token pasted into the base lands in the path or query as readily as in the userinfo, so the
        # refusal says which part is present and never quotes it.
        result = self.run_curl(
            base="http://localhost:5141/path-secret-0123?token=query-secret#frag-secret")
        self.assert_refused(result)
        for part in ("path=present", "query=present", "fragment=present", "userinfo=absent"):
            self.assertIn(part, result.stderr)
        for secret in ("path-secret-0123", "query-secret", "frag-secret"):
            self.assertNotIn(secret, result.stderr + result.stdout)

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
        self.assertIn("--fail-with-body", argv)
        self.assertFalse(any(TOKEN in arg for arg in argv), "token reached curl argv")
        header = (self.dir / "curl.header").read_text(encoding="utf-8")
        self.assertEqual(header, "Authorization: Bearer {}\n".format(TOKEN))
        self.assertEqual((self.dir / "curl.mode").read_text(encoding="utf-8").strip(), "-rw-------")
        header_path = Path((self.dir / "curl.headerpath").read_text(encoding="utf-8").strip())
        self.assertFalse(header_path.exists(), "header file outlived the request")

    def test_curl_is_started_with_q_as_its_first_argument(self):
        result = self.run_curl()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.curl_argv()[0], "-q")

    def test_every_curl_invocation_in_the_script_opens_with_q(self):
        # Static, so a curl call the dynamic cases never reach is held to the same rule.
        calls = [line.split() for line in SCRIPT.read_text(encoding="utf-8").splitlines()
                 if line.split()[:1] == ["curl"]]
        self.assertTrue(calls, "the script no longer invokes curl, so this check is vacuous")
        for words in calls:
            with self.subTest(call=" ".join(words)):
                self.assertEqual(words[1], "-q")

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

    # --- real curl: a default config file is ignored ----------------------------------------------

    def _curlrc_home(self):
        # A `.curlrc` that redirects curl's stderr into a canary file: if the file appears, curl read
        # the config. Port 9 on loopback refuses at once, so no request leaves the machine.
        home = self.dir / "curl-home"
        home.mkdir()
        canary = home / "curlrc-was-read"
        (home / ".curlrc").write_text('stderr = "{}"\n'.format(canary), encoding="utf-8")
        return home, canary

    def _real_curl_env(self, home):
        return {"PATH": os.environ.get("PATH", ""), "HOME": str(home), "TMPDIR": str(self.tmp)}

    @unittest.skipUnless(shutil.which("curl"), "curl is not installed")
    def test_the_curlrc_canary_is_read_by_a_curl_started_without_q(self):
        # The control: without it, the next case would pass for a canary curl never honours.
        home, canary = self._curlrc_home()
        subprocess.run(["curl", "-sS", "http://127.0.0.1:9/"], env=self._real_curl_env(home),
                       capture_output=True, text=True, timeout=30)
        self.assertTrue(canary.exists(), "this curl does not read $HOME/.curlrc; the probe is moot")

    @unittest.skipUnless(shutil.which("curl"), "curl is not installed")
    def test_real_curl_ignores_a_default_curlrc(self):
        home, canary = self._curlrc_home()
        env = self._real_curl_env(home)
        env["CONTEXT_MEMORY_BASE_URL"] = "http://127.0.0.1:9"
        script = 'source "$1"; recall_feedback_curl GET "$2" "$3"'
        result = subprocess.run(["bash", "-c", script, "harness", str(SCRIPT), READ_PATH, TOKEN],
                                env=env, capture_output=True, text=True, timeout=30)
        self.assertNotEqual(result.returncode, 0, "nothing listens on port 9")
        self.assertIn("curl:", result.stderr, "curl did not run: " + result.stderr)
        self.assertFalse(canary.exists(), "curl read the default .curlrc despite -q")

    # --- real curl: a refusal is a failure, not an empty success -----------------------------------

    @unittest.skipUnless(CURL_HAS_FAIL_WITH_BODY, "curl is absent or older than 7.76")
    def test_real_curl_turns_a_403_into_a_failure_that_keeps_the_body(self):
        # The fake curl answers whatever it is told, so only a real curl shows what the flags do: without
        # --fail-with-body a 403 exits 0 and a wrong token reads as "no findings"; with plain --fail
        # the exit is non-zero but the refusal's body is lost.
        seen = []

        class Refuse(http.server.BaseHTTPRequestHandler):
            def do_GET(self):
                seen.append(self.headers.get("Authorization"))
                body = b'{"error":"forbidden-canary"}'
                self.send_response(403)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def log_message(self, *args):
                pass

        server = http.server.HTTPServer(("127.0.0.1", 0), Refuse)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            home = self.dir / "curl-home"
            home.mkdir()
            env = self._real_curl_env(home)
            env["CONTEXT_MEMORY_BASE_URL"] = "http://127.0.0.1:{}".format(server.server_address[1])
            script = 'source "$1"; recall_feedback_curl GET "$2" "$3"'
            result = subprocess.run(
                ["bash", "-c", script, "harness", str(SCRIPT), READ_PATH, TOKEN],
                env=env, capture_output=True, text=True, timeout=30)
        finally:
            server.shutdown()
            server.server_close()
            thread.join(timeout=10)
        self.assertEqual(seen, ["Bearer " + TOKEN], "the request never reached the responder")
        self.assertEqual(result.returncode, 22, result.stderr)
        self.assertIn("forbidden-canary", result.stdout)
        self.assertEqual(list(self.tmp.iterdir()), [], "header file outlived the refused request")

    # --- documentation stays wired to the tested code ---------------------------------------------

    def test_skill_sources_the_tested_script(self):
        skill = (SKILL / "SKILL.md").read_text(encoding="utf-8")
        self.assertIn("scripts/recall_feedback.sh", skill)
        self.assertNotIn("recall_feedback_guard() {", skill,
                         "SKILL.md carries its own copy of the guard instead of sourcing the script")

    def _documented_queries(self):
        skill = (SKILL / "SKILL.md").read_text(encoding="utf-8")
        blocks = re.findall(r"```bash\n(.*?)```", skill, re.S)
        queries = [b.replace("\\\n", " ").strip() for b in blocks if "recall_feedback_curl GET" in b]
        self.assertEqual(len(queries), 2, "expected the never-recalled and miss-rate queries")
        return queries

    def _run_documented(self, query, extra_env):
        env = {
            "PATH": "{}:{}".format(self.bin, os.environ.get("PATH", "")),
            "HOME": str(self.dir),
            "TMPDIR": str(self.tmp),
            "RF_TEST_DIR": str(self.dir),
            "RF_REAL_PYTHON": sys.executable,
            "CONTEXT_MEMORY_READ_TOKEN": TOKEN,
        }
        env.update(extra_env)
        return subprocess.run(["bash", "-c", 'source "$1"\n' + query, "harness", str(SCRIPT)],
                              env=env, capture_output=True, text=True, timeout=30)

    def test_documented_queries_carry_no_fixed_date(self):
        # A copied fixed window re-measures the same past period on every run, so a tuning change can
        # never show up in the before/after comparison.
        for query in self._documented_queries():
            with self.subTest(query=query):
                self.assertIsNone(re.search(r"\d{4}-\d{2}-\d{2}", query), query)

    def test_documented_queries_refuse_to_send_without_the_operator_window(self):
        for query in self._documented_queries():
            with self.subTest(query=query):
                result = self._run_documented(query, {})
                self.assertNotEqual(result.returncode, 0, result.stderr)
                self.assertFalse(self.curl_called(), "a query ran without an operator-set window")

    def test_documented_queries_send_the_operator_window(self):
        window = {"RF_FROM": "2030-01-01", "RF_TO": "2030-01-08"}
        sent = []
        for query in self._documented_queries():
            result = self._run_documented(query, window)
            self.assertEqual(result.returncode, 0, result.stderr)
            sent.append(self.curl_argv()[-1])
        self.assertTrue(any("asOf=2030-01-08" in url for url in sent), sent)
        self.assertTrue(any("from=2030-01-01&to=2030-01-08" in url for url in sent), sent)


if __name__ == "__main__":
    unittest.main(verbosity=2)
