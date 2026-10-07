#!/usr/bin/env python3
"""Committed L0 harness for the mimisbrunnr-muninn-recall-feedback request guard.

Run: python3 -B .agents/skills/mimisbrunnr-muninn-recall-feedback/tests/run_tests.py

Sources and executes scripts/recall_feedback.sh in bash with a fake `curl` (and a recording `python3`
shim) first on PATH, so no request leaves the machine. Covers:
  - a non-loopback origin and a userinfo origin are refused before any request, and the refusal does
    not echo the credential, the scheme or the hostname;
  - an origin carrying `;params` is refused (urlparse moves them out of the path);
  - a path that would move the host (`@host/...`, `//host/...`, a scheme, whitespace) is refused before
    any request;
  - every curl call opens with `-q`, so a default `.curlrc` cannot alter the request (the fake curl
    refuses any call that does not, and a real-curl case proves a `.curlrc` canary is ignored);
  - an accepted request carries `--noproxy '*'`, sends the token on curl's stdin (`-H @-`) — never on
    argv and never in a file, so TMPDIR stays empty while curl runs, after a failure and on interrupt;
  - a `403` is a non-zero exit with its body kept: the option reaches the fake curl, and a real curl
    against a loopback responder proves the behaviour (skipped when curl lacks `--fail-with-body`);
  - the base URL never reaches any process's argv;
  - the command line: each command builds its own path and picks its own token, and a missing,
    empty or malformed date, an unknown option, or an option the command does not take sends nothing
    and is not echoed; sourcing the script runs nothing;
  - SKILL.md runs this script rather than carrying its own copy of the guard, and its documented
    commands take their window from operator-supplied dates instead of a fixed date.

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
# Records argv one element per line, the header it was given (from stdin for `@-`, else from the named
# file), and what TMPDIR held at call time — a header file written for the request is there then.
# Refuses any call whose first argument is not -q: without it a real curl reads ~/.curlrc first, so
# every test that reaches curl also proves the default config is disabled.
if [ "${1:-}" != "-q" ]; then echo "fake curl: -q is not argv[1]" >&2; exit 97; fi
log="$RF_TEST_DIR/curl.argv"
: >"$log"
for arg in "$@"; do printf '%s\\n' "$arg" >>"$log"; done
ls -A "$TMPDIR" >"$RF_TEST_DIR/curl.tmpdir"
prev=""
for arg in "$@"; do
  if [ "$prev" = "-H" ]; then
    case "$arg" in
      @-) cat >"$RF_TEST_DIR/curl.header" ;;
      @*) cp "${arg#@}" "$RF_TEST_DIR/curl.header" ;;
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


class FakeCurlCase(unittest.TestCase):
    """A scratch HOME/TMPDIR with the recording fake `curl` and `python3` shim first on PATH."""

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

    def curl_called(self):
        return (self.dir / "curl.argv").exists()

    def curl_argv(self):
        return (self.dir / "curl.argv").read_text(encoding="utf-8").splitlines()


class RecallFeedbackGuardTests(FakeCurlCase):

    def run_curl(self, path=READ_PATH, token=TOKEN, base=None, extra_env=None, method="GET"):
        # The helper takes a capability and reads the token from the environment (review #14): a GET
        # is a read, anything else a write, and the token is set in the matching variable.
        capability = "read" if method == "GET" else "write"
        env = {
            "PATH": "{}:{}".format(self.bin, os.environ.get("PATH", "")),
            "HOME": str(self.dir),
            "TMPDIR": str(self.tmp),
            "RF_TEST_DIR": str(self.dir),
            "RF_REAL_PYTHON": sys.executable,
        }
        if base is not None:
            env["CONTEXT_MEMORY_BASE_URL"] = base
        if token:
            env["CONTEXT_MEMORY_READ_TOKEN" if capability == "read" else "CONTEXT_MEMORY_WRITE_TOKEN"] = token
        env.update(extra_env or {})
        script = textwrap.dedent("""\
            source "$1"
            recall_feedback_curl "$2" "$3" "$4"
            """)
        return subprocess.run(
            ["bash", "-c", script, "harness", str(SCRIPT), method, path, capability],
            env=env, capture_output=True, text=True, timeout=30)

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

    def test_params_bearing_origin_is_refused_without_echoing_them(self):
        # urlparse moves `;params` out of the path, so a guard reading only path/query/fragment took
        # this for a bare origin and curl sent the params in the request line (issue 182).
        for base in ("http://localhost:5141/;tok=params-secret-0123",
                     "http://localhost:5141/;x@evil.example"):
            with self.subTest(base=base):
                result = self.run_curl(base=base)
                self.assert_refused(result)
                self.assertIn("params=present", result.stderr)
                self.assertNotIn("params-secret-0123", result.stderr + result.stdout)
                self.assertNotIn("evil.example", result.stderr + result.stdout)

    def test_no_call_site_expands_a_token_into_an_argument(self):
        """Review #14: the documented calls expanded the token into the helper's arguments. They now
        run the script with dates only, and the script reads the token from the environment."""
        doc = (SCRIPT.parents[1] / "SKILL.md").read_text(encoding="utf-8")
        calls = [b for b in re.findall(r"```bash\n(.*?)```", doc, re.S) if "recall_feedback" in b]
        self.assertTrue(calls)
        for call in calls:
            with self.subTest(call=call.strip()[:60]):
                self.assertNotIn("$CONTEXT_MEMORY_", call)
                self.assertNotIn("TOKEN", call)
                self.assertNotIn("recall_feedback_curl", call)

    def test_an_unknown_capability_sends_nothing(self):
        script = 'source "$1"; recall_feedback_curl GET "$2" "$3"'
        env = {"PATH": "{}:{}".format(self.bin, os.environ.get("PATH", "")), "HOME": str(self.dir),
               "TMPDIR": str(self.tmp), "RF_TEST_DIR": str(self.dir), "RF_REAL_PYTHON": sys.executable,
               "CONTEXT_MEMORY_READ_TOKEN": TOKEN}
        result = subprocess.run(["bash", "-c", script, "harness", str(SCRIPT), READ_PATH, TOKEN],
                                env=env, capture_output=True, text=True, timeout=30)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("capability must be 'read' or 'write'", result.stderr)
        self.assertFalse(self.curl_called())

    def test_an_empty_delimiter_is_refused(self):
        # Issue 188: `urlparse` reads `http://localhost:5141?` as an empty query, so the bare-origin
        # guard passed it; the delimiter itself is refused and reported as present.
        for base, part in (("http://localhost:5141?", "query=present"),
                           ("http://localhost:5141#", "fragment=present"),
                           ("http://localhost:5141/;", "params=present")):
            with self.subTest(base=base):
                result = self.run_curl(base=base)
                self.assert_refused(result)
                self.assertIn(part, result.stderr)

    def test_refusal_never_echoes_the_scheme_or_the_hostname(self):
        # A token pasted into the wrong variable parses as a scheme (`admin:hunter2` -> `admin`) or as
        # a hostname, so both are reported as acceptable or not, never quoted (issue 182).
        cases = (("scheme-secret-0123:hunter2", "scheme-secret-0123"),
                 ("http://host-secret-0123.example:5141", "host-secret-0123"))
        for base, secret in cases:
            with self.subTest(base=base):
                result = self.run_curl(base=base)
                self.assert_refused(result)
                self.assertNotIn(secret, (result.stderr + result.stdout).lower())
        self.assertIn("scheme=invalid", self.run_curl(base="ftp://localhost").stderr)

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

    def test_the_token_is_never_written_to_a_file(self):
        """Issue 184: SKILL.md promises the token is never written to a file, while the request wrote
        it to a mode-600 temp header file that a SIGKILL would leave behind. The header now reaches
        curl on stdin, so TMPDIR is empty while curl runs and no `-H @<file>` names a path."""
        for method, token in (("GET", TOKEN), ("POST", "synthetic-write-token-9876543210")):
            with self.subTest(method=method):
                result = self.run_curl(method=method, token=token,
                                       path="/api/context/recall-feedback/reset"
                                       if method == "POST" else READ_PATH)
                self.assertEqual(result.returncode, 0, result.stderr)
                argv = self.curl_argv()
                self.assertEqual(argv[argv.index("-H") + 1], "@-")
                self.assertEqual((self.dir / "curl.tmpdir").read_text(encoding="utf-8"), "",
                                 "a file existed in TMPDIR while curl ran")
                self.assertEqual((self.dir / "curl.header").read_text(encoding="utf-8"),
                                 "Authorization: Bearer {}\n".format(token))

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
        env["CONTEXT_MEMORY_READ_TOKEN"] = TOKEN
        script = 'source "$1"; recall_feedback_curl GET "$2" read'
        result = subprocess.run(["bash", "-c", script, "harness", str(SCRIPT), READ_PATH],
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
            env["CONTEXT_MEMORY_READ_TOKEN"] = TOKEN
            script = 'source "$1"; recall_feedback_curl GET "$2" read'
            result = subprocess.run(
                ["bash", "-c", script, "harness", str(SCRIPT), READ_PATH],
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

    def _documented_commands(self):
        """The script invocations SKILL.md tells the operator to run, keyed by command. Issue 190 #18:
        a wiring test that only looked for the path anywhere in the file stayed green with the
        documented step deleted, so each command is read from a fenced block and run as written."""
        skill = (SKILL / "SKILL.md").read_text(encoding="utf-8")
        suffix = "mimisbrunnr-muninn-recall-feedback/scripts/recall_feedback.sh"
        commands = {}
        for block in re.findall(r"```bash\n(.*?)```", skill, re.S):
            for line in block.replace("\\\n", " ").splitlines():
                words = line.split()
                if words and words[0].endswith(suffix):
                    self.assertNotIn(words[1], commands, "a command is documented twice")
                    commands[words[1]] = words[2:]
        self.assertEqual(sorted(commands), ["guard", "miss-rate", "never-recalled", "reset"], commands)
        return commands

    def test_skill_runs_the_tested_script(self):
        skill = (SKILL / "SKILL.md").read_text(encoding="utf-8")
        self._documented_commands()
        self.assertNotIn("recall_feedback_guard() {", skill,
                         "SKILL.md carries its own copy of the guard instead of running the script")
        self.assertNotIn("source .agents/skills/mimisbrunnr-muninn-recall-feedback", skill,
                         "SKILL.md still sources the script instead of running it")

    def test_the_script_is_executable(self):
        self.assertTrue(os.access(SCRIPT, os.X_OK), "SKILL.md runs the script, so it must be executable")

    def _run_documented(self, args, extra_env):
        env = {
            "PATH": "{}:{}".format(self.bin, os.environ.get("PATH", "")),
            "HOME": str(self.dir),
            "TMPDIR": str(self.tmp),
            "RF_TEST_DIR": str(self.dir),
            "RF_REAL_PYTHON": sys.executable,
            "CONTEXT_MEMORY_READ_TOKEN": TOKEN,
        }
        env.update(extra_env)
        return subprocess.run(["bash", str(SCRIPT)] + list(args),
                              env=env, capture_output=True, text=True, timeout=30)

    def _queries(self):
        commands = self._documented_commands()
        return {name: commands[name] for name in ("never-recalled", "miss-rate")}

    def test_documented_queries_carry_no_fixed_date(self):
        # A copied fixed window re-measures the same past period on every run, so a tuning change can
        # never show up in the before/after comparison.
        for name, args in self._queries().items():
            with self.subTest(query=name):
                self.assertIsNone(re.search(r"\d{4}-\d{2}-\d{2}", " ".join(args)), args)

    def test_documented_queries_refuse_to_send_without_the_operator_window(self):
        for name, args in self._queries().items():
            with self.subTest(query=name):
                result = self._run_documented([name] + args, {})
                self.assertEqual(result.returncode, 2, result.stderr)
                self.assertFalse(self.curl_called(), "a query ran with the placeholder date")

    def test_documented_queries_send_the_operator_window(self):
        sent = []
        for name, args in self._queries().items():
            dates = iter(["2030-01-01", "2030-01-08"] if "--from" in args else ["2030-01-08"])
            filled = [next(dates) if a == "YYYY-MM-DD" else a for a in args]
            result = self._run_documented([name] + filled, {})
            self.assertEqual(result.returncode, 0, result.stderr)
            sent.append(self.curl_argv()[-1])
        self.assertTrue(any("asOf=2030-01-08&limit=500" in url for url in sent), sent)
        self.assertTrue(any("from=2030-01-01&to=2030-01-08" in url for url in sent), sent)


class RecallFeedbackCommandLineTests(FakeCurlCase):
    """The executable entry point: what each command sends, and what it refuses before sending."""

    def run_cli(self, *args, read=TOKEN, write=None, base=None):
        env = {
            "PATH": "{}:{}".format(self.bin, os.environ.get("PATH", "")),
            "HOME": str(self.dir),
            "TMPDIR": str(self.tmp),
            "RF_TEST_DIR": str(self.dir),
            "RF_REAL_PYTHON": sys.executable,
        }
        if read:
            env["CONTEXT_MEMORY_READ_TOKEN"] = read
        if write:
            env["CONTEXT_MEMORY_WRITE_TOKEN"] = write
        if base is not None:
            env["CONTEXT_MEMORY_BASE_URL"] = base
        return subprocess.run([str(SCRIPT)] + list(args), env=env, capture_output=True, text=True,
                              timeout=30)

    def sent(self):
        argv = self.curl_argv()
        header = (self.dir / "curl.header").read_text(encoding="utf-8")
        return argv[argv.index("-X") + 1], argv[-1], header

    def test_never_recalled_sends_the_window_end_and_default_limit_with_the_read_token(self):
        result = self.run_cli("never-recalled", "--to", "2030-01-08", write="write-token-unused")
        self.assertEqual(result.returncode, 0, result.stderr)
        method, url, header = self.sent()
        self.assertEqual(method, "GET")
        self.assertEqual(url, "http://localhost:5141/api/context/recall-feedback/never-recalled"
                              "?asOf=2030-01-08&limit=500")
        self.assertEqual(header, "Authorization: Bearer {}\n".format(TOKEN))

    def test_never_recalled_takes_a_limit_and_a_utc_instant(self):
        result = self.run_cli("never-recalled", "--to", "2030-01-08T12:00:00.5Z", "--limit", "25")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue(self.sent()[1].endswith("asOf=2030-01-08T12:00:00.5Z&limit=25"), self.sent()[1])

    def test_a_real_leap_day_is_accepted(self):
        result = self.run_cli("miss-rate", "--from", "2028-02-29", "--to", "2028-03-01")
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_older_curl_guidance_keeps_a_failing_exit(self):
        """Consumer review 5438563690 #8: the docs said to drop `--fail-with-body` on older curl, which
        makes a 403 exit 0. The fallback is `--fail`."""
        for doc in ("SKILL.md", "AGENTS.md"):
            text = " ".join((SKILL / doc).read_text(encoding="utf-8").split())
            with self.subTest(doc=doc):
                self.assertNotIn("drop that one flag", text)
                self.assertIn("`--fail`", text)

    def test_miss_rate_sends_both_dates(self):
        result = self.run_cli("miss-rate", "--from", "2030-01-01", "--to", "2030-01-08")
        self.assertEqual(result.returncode, 0, result.stderr)
        method, url, _ = self.sent()
        self.assertEqual(method, "GET")
        self.assertTrue(url.endswith("/miss-rate?from=2030-01-01&to=2030-01-08"), url)

    def test_reset_posts_with_the_write_token_only(self):
        result = self.run_cli("reset", write="synthetic-write-token-0123")
        self.assertEqual(result.returncode, 0, result.stderr)
        method, url, header = self.sent()
        self.assertEqual(method, "POST")
        self.assertTrue(url.endswith("/api/context/recall-feedback/reset"), url)
        self.assertEqual(header, "Authorization: Bearer synthetic-write-token-0123\n")

    def test_reset_with_only_a_read_token_sends_nothing(self):
        result = self.run_cli("reset")
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("no write token", result.stderr)
        self.assertFalse(self.curl_called())

    def test_a_query_with_only_a_write_token_sends_nothing(self):
        result = self.run_cli("miss-rate", "--from", "2030-01-01", "--to", "2030-01-08", read=None,
                              write="synthetic-write-token-0123")
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("no read token", result.stderr)
        self.assertFalse(self.curl_called())

    def test_guard_sends_nothing(self):
        result = self.run_cli("guard")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("origin approved", result.stdout)
        self.assertFalse(self.curl_called())
        refused = self.run_cli("guard", base="http://evil.example")
        self.assertEqual(refused.returncode, 1)
        self.assertNotIn("evil.example", refused.stderr + refused.stdout)

    def test_bad_arguments_send_nothing_and_are_not_echoed(self):
        secret = "pasted-secret-0123"
        cases = [
            (),
            ("bogus",),
            (secret,),
            ("never-recalled",),
            ("never-recalled", "--to"),
            ("never-recalled", "--to", ""),
            ("never-recalled", "--to", secret),
            ("never-recalled", "--to", "2030-13"),
            ("never-recalled", "--to", "2030-01-08&limit=1"),
            ("never-recalled", "--to", "2030-01-08T00:00:00+01:00"),
            ("never-recalled", "--to", "2030-02-30"),
            ("never-recalled", "--to", "2030-01-08T24:00:00Z"),
            ("miss-rate", "--from", "2030-02-29", "--to", "2030-03-01"),
            ("never-recalled", "--to", "2030-01-08", "--limit", "0"),
            ("never-recalled", "--to", "2030-01-08", "--limit", "10001"),
            ("never-recalled", "--to", "2030-01-08", "--limit", ""),
            ("never-recalled", "--to", "2030-01-08", "--from", "2030-01-01"),
            ("never-recalled", "--to", "2030-01-08", secret),
            ("miss-rate", "--from", "2030-01-01"),
            ("miss-rate", "--to", "2030-01-08"),
            ("miss-rate", "--from", secret, "--to", "2030-01-08"),
            ("reset", "--to", "2030-01-08"),
            ("reset", "--limit", "500"),
            ("guard", "--from", "2030-01-01"),
        ]
        for args in cases:
            with self.subTest(args=args):
                # One case's request must not read as the next case's: each judges a fresh log.
                (self.dir / "curl.argv").unlink(missing_ok=True)
                result = self.run_cli(*args, write="synthetic-write-token-0123")
                self.assertEqual(result.returncode, 2, result.stderr)
                self.assertFalse(self.curl_called(), "a request was sent")
                self.assertNotIn(secret, result.stderr + result.stdout)

    def test_help_lists_every_command(self):
        result = self.run_cli("--help")
        self.assertEqual(result.returncode, 0)
        for command in ("never-recalled", "miss-rate", "reset", "guard"):
            self.assertIn(command, result.stdout)

    def test_sourcing_runs_nothing(self):
        env = {"PATH": "{}:{}".format(self.bin, os.environ.get("PATH", "")), "HOME": str(self.dir),
               "TMPDIR": str(self.tmp), "RF_TEST_DIR": str(self.dir), "RF_REAL_PYTHON": sys.executable,
               "CONTEXT_MEMORY_WRITE_TOKEN": TOKEN}
        result = subprocess.run(["bash", "-c", 'source "$1" reset; echo sourced', "harness", str(SCRIPT)],
                                env=env, capture_output=True, text=True, timeout=30)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout.strip(), "sourced")
        self.assertFalse(self.curl_called())

if __name__ == "__main__":
    unittest.main(verbosity=2)
