#!/usr/bin/env python3
"""Committed L0 harness for the Heimdallr session-metadata scan.

Run: python3 -B .agents/skills/mimisbrunnr-heimdallr-find-session-metadata/tests/run_tests.py

Puts a fake `git` on PATH (no checkout touched) and asserts: repo parsing across
URL shapes, ticket extraction with sources, initiative flag-or-unknown, and the
console-only contract (no file created, --json parses). stdlib unittest.
"""

from __future__ import annotations

import json
import os
import shutil
import stat
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SKILL = Path(__file__).resolve().parents[1]
SCRIPT = SKILL / "scripts" / "find_session_metadata.py"
# The shared synthetic fixture: the redactor, the decision gate and this reporter read one list.
CREDENTIAL_LIKE = json.loads(
    (SKILL.parent / "mimisbrunnr-odin-context-memory" / "tests" / "fixtures" / "credential_like.json")
    .read_text(encoding="utf-8"))


def run_with_git(responses: dict, *argv: str, cwd: str | None = None,
                 script: Path = SCRIPT) -> subprocess.CompletedProcess:
    """Run the script with a fake git answering from `responses` keyed by argv tail."""
    tmp = tempfile.mkdtemp()
    # The script runs in `cwd` when given (so a test can observe files it writes there); the fake git
    # and responses live in their own dir, which is prepended to PATH, not the script's cwd.
    run_cwd = cwd if cwd is not None else tmp
    calls = Path(tmp) / "git.calls"
    mapping = Path(tmp) / "responses.json"
    mapping.write_text(json.dumps(responses), encoding="utf-8")
    fake = Path(tmp) / "git"
    fake.write_text(
        "#!/usr/bin/env python3\n"
        "import json, sys\n"
        "key = ' '.join(sys.argv[1:])\n"
        "open(%r, 'a').write(key + chr(10))\n"
        "data = json.load(open(%r))\n"
        "if key == 'rev-parse --is-inside-work-tree':\n"
        "    if data.get('_no_work_tree'):\n"
        "        sys.stderr.write('fatal: not a git repository\\n')\n"
        "        sys.exit(128)\n"
        "    sys.stdout.write('false\\n' if data.get('_bare') else 'true\\n')\n"
        "    sys.exit(0)\n"
        "if key in data:\n"
        "    sys.stdout.write(data[key])\n"
        "    sys.exit(0)\n"
        "sys.exit(1)\n" % (str(calls), str(mapping)),
        encoding="utf-8",
    )
    fake.chmod(fake.stat().st_mode | stat.S_IXUSR)
    env = dict(os.environ, PATH=tmp + os.pathsep + os.environ.get("PATH", ""))
    try:
        return subprocess.run(
            [sys.executable, "-B", str(script), *argv],
            capture_output=True, text=True, encoding="utf-8", env=env, cwd=run_cwd,
        )
    finally:
        import shutil

        shutil.rmtree(tmp, ignore_errors=True)


class RepoTests(unittest.TestCase):
    def _repo(self, remote: str, branch: str = "main") -> str:
        proc = run_with_git(
            {
                "remote get-url origin": remote + "\n",
                "branch --show-current": branch + "\n",
                f"log {branch} --format=%s -n 10": "\n",
            },
            "--json",
        )
        self.assertEqual(proc.returncode, 0, proc.stderr)
        return json.loads(proc.stdout)["repository"]

    def test_https(self):
        self.assertEqual(
            self._repo("https://github.com/acme/widgets.git"), "acme/widgets"
        )

    def test_ssh(self):
        self.assertEqual(
            self._repo("git@github.com:acme/widgets.git"), "acme/widgets"
        )

    def test_unprovable(self):
        self.assertIsNone(self._repo(""))

    def test_nested_group_path_is_kept_whole(self):
        # A GitLab-style remote is one repository `group/subgroup/repo`, not just the last pair; the
        # last-pair form silently dropped the parent group and returned a wrong id.
        self.assertEqual(
            self._repo("https://gitlab.com/group/subgroup/repo.git"), "group/subgroup/repo"
        )

    def test_trailing_slash_and_dots_do_not_defeat_the_path(self):
        # Some remotes append a trailing slash; the repo name may also contain dots/hyphens.
        self.assertEqual(self._repo("https://github.com/acme/my.repo.git/"), "acme/my.repo")
        self.assertEqual(self._repo("git@gitlab.com:group/sub.repo.git"), "group/sub.repo")

    def test_ssh_port_and_query_do_not_defeat_the_path(self):
        # A self-hosted tracker may listen on a non-standard SSH port; the port must not become a
        # repo path segment. A query/fragment must not defeat the path either.
        self.assertEqual(self._repo("ssh://git@host:2222/group/sub/repo.git"), "group/sub/repo")
        self.assertEqual(self._repo("https://gitlab.com/group/sub/repo.git?ref=x"), "group/sub/repo")

    def test_not_a_git_repo_reports_unavailable_not_empty(self):
        # A non-git checkout must signal "autofill unavailable" (exit 2) rather than masquerade as a
        # genuine empty recall (exit 0 with no tickets), which a caller cannot distinguish.
        proc = run_with_git({"_no_work_tree": "1"}, "--json")
        self.assertEqual(proc.returncode, 2, proc.stderr)
        self.assertIn("git unavailable", proc.stderr)

    def test_a_bare_or_inside_git_dir_reports_unavailable_not_empty(self):
        # `git rev-parse --is-inside-work-tree` answers "false" (exit 0) in a bare clone or inside
        # `.git/`; that is not an error, so a probe keyed on `is None` would let it through as "no
        # tickets". The guard checks for the literal "true", so this is also unavailable.
        proc = run_with_git({"_bare": "1"}, "--json")
        self.assertEqual(proc.returncode, 2, proc.stderr)
        self.assertIn("git unavailable", proc.stderr)


class TicketTests(unittest.TestCase):
    def _scan(self, branch: str, log: str, *extra: str) -> dict:
        proc = run_with_git(
            {
                "remote get-url origin": "https://github.com/acme/widgets.git\n",
                "branch --show-current": branch + "\n",
                f"log {branch} --format=%s -n 10": log,
            },
            "--json",
            *extra,
        )
        self.assertEqual(proc.returncode, 0, proc.stderr)
        return json.loads(proc.stdout)

    def test_branch_hash_is_github(self):
        result = self._scan("feat/160-x", "")
        self.assertIn(
            {"provider": "github", "key": "160", "seenIn": "branch"},
            result["tickets"],
        )

    def test_provider_key_and_bare(self):
        result = self._scan("main", "fix jira:ABC-123\ntouch linear:XYZ-42\n")
        providers = {(t["provider"], t["key"]) for t in result["tickets"]}
        self.assertIn(("jira", "ABC-123"), providers)
        self.assertIn(("linear", "XYZ-42"), providers)
        # The bare-key pattern matches ABC-123 inside `jira:ABC-123`; it must not be re-emitted as a
        # spurious `local:ABC-123`, which the dedupe (keyed on provider:key) would not collapse.
        self.assertNotIn(("local", "ABC-123"), providers)
        self.assertNotIn(("local", "XYZ-42"), providers)

    def test_dedupe_keeps_first_source(self):
        result = self._scan("feat/160-x", "revisit #160\n")
        hits = [t for t in result["tickets"] if t["key"] == "160"]
        self.assertEqual(len(hits), 1)
        self.assertEqual(hits[0]["seenIn"], "branch")

    def test_tickets_within_one_subject_keep_first_seen_order(self):
        # Pattern order would list #456 (the first pattern) before ABC-123 and jira:XYZ-9, although
        # the subject names them the other way round.
        result = self._scan("main", "fix ABC-123 then jira:XYZ-9 and #456\n")
        self.assertEqual(
            [(t["provider"], t["key"]) for t in result["tickets"]],
            [("local", "ABC-123"), ("jira", "XYZ-9"), ("github", "456")],
        )

    def test_initiative_flag_or_unknown(self):
        self.assertEqual(self._scan("main", "")["initiative"], "unknown")
        self.assertEqual(
            self._scan("main", "", "--initiative", "mimisbrunnr")["initiative"],
            "mimisbrunnr",
        )


class CredentialShapedTicketTests(unittest.TestCase):
    """Issue 182: `provider:key` accepts any `word:value`, so a credential in a commit subject was printed
    as a ticket and kvasir's autofill could bind it. Every candidate now passes the capture skill's
    redactor; a dropped one is counted, never shown."""

    def _scan(self, log: str, *extra: str, script: Path = SCRIPT) -> subprocess.CompletedProcess:
        proc = run_with_git(
            {
                "remote get-url origin": "https://github.com/acme/widgets.git\n",
                "branch --show-current": "main\n",
                "log main --format=%s -n 10": log,
            },
            *extra,
            script=script,
        )
        self.assertEqual(proc.returncode, 0, proc.stderr)
        return proc

    def test_every_credential_fixture_yields_no_ticket_and_is_never_printed(self):
        for entry in CREDENTIAL_LIKE["credentials"]:
            for flags in ((), ("--json",)):
                with self.subTest(id=entry["id"], gate=entry["gate"], json=bool(flags)):
                    proc = self._scan(f"chore: rotate {entry['text']} today\n", *flags)
                    self.assertNotIn(entry["secret"], proc.stdout + proc.stderr)
                    if flags:
                        result = json.loads(proc.stdout)
                        self.assertEqual(result["tickets"], [])
                        self.assertEqual(result["ticketsWithheld"], 1)
                        self.assertIsNone(result["ticketsUnavailable"])
                    else:
                        self.assertIn("withheld: 1 credential-shaped candidate(s), not shown",
                                      proc.stdout)

    def test_every_control_is_still_a_ticket(self):
        for entry in CREDENTIAL_LIKE["controls"]:
            with self.subTest(id=entry["id"]):
                provider, key = entry["text"].split(":", 1)
                result = json.loads(self._scan(f"fix {entry['text']}\n", "--json").stdout)
                self.assertIn({"provider": provider, "key": key, "seenIn": "commit"},
                              result["tickets"])
                self.assertEqual(result["ticketsWithheld"], 0)

    def test_a_bare_key_inside_a_secret_assignment_is_withheld(self):
        # The candidate alone is an ordinary key; only its subject shows it is a password's value.
        result = json.loads(self._scan("fix password=PROJ-1234567 leak\n", "--json").stdout)
        self.assertEqual(result["tickets"], [])
        self.assertEqual(result["ticketsWithheld"], 1)

    def test_without_the_redactor_no_ticket_is_reported(self):
        """Fail closed: the reporter installed without the capture skill beside it reports no ticket
        at all — controls included — and says why, rather than reporting unchecked ones."""
        with tempfile.TemporaryDirectory() as tmp:
            lone = Path(tmp) / "skills" / "mimisbrunnr-heimdallr-find-session-metadata" / "scripts"
            lone.mkdir(parents=True)
            shutil.copy(SCRIPT, lone / SCRIPT.name)
            text = CREDENTIAL_LIKE["credentials"][0]["text"]
            proc = self._scan(f"fix github:182 and {text}\n", "--json", script=lone / SCRIPT.name)
            result = json.loads(proc.stdout)
            self.assertEqual(result["tickets"], [])
            self.assertIn("redactor unavailable", result["ticketsUnavailable"])
            self.assertEqual(result["repository"], "acme/widgets")
            self.assertNotIn(CREDENTIAL_LIKE["credentials"][0]["secret"], proc.stdout + proc.stderr)
            human = self._scan("fix github:182\n", script=lone / SCRIPT.name).stdout
            self.assertIn("tickets: unavailable (redactor unavailable", human)


class ContractTests(unittest.TestCase):
    def test_human_default_and_no_files(self):
        tmp = tempfile.mkdtemp()
        try:
            before = set(os.listdir(tmp))
            proc = run_with_git(
                {
                    "remote get-url origin": "https://github.com/acme/widgets.git\n",
                    "branch --show-current": "feat/160-x\n",
                    "log feat/160-x --format=%s -n 10": "\n",
                },
                cwd=tmp,
            )
            self.assertEqual(proc.returncode, 0, proc.stderr)
            self.assertIn("repository: acme/widgets", proc.stdout)
            self.assertIn("github:160", proc.stdout)
            self.assertEqual(set(os.listdir(tmp)), before)
        finally:
            import shutil

            shutil.rmtree(tmp, ignore_errors=True)


class StabilityTests(unittest.TestCase):
    def test_two_consecutive_runs_on_the_same_git_state_are_byte_identical(self):
        """The log window is pinned to the branch ref, so identical git state is reproducible.

        An implicit-HEAD `git log` reads whichever tip the checkout sits on, so the same branch
        could yield a different ticket between invocations. Pinning the ref makes the output a pure
        function of the branch state.
        """
        responses = {
            "remote get-url origin": "https://github.com/acme/widgets.git\n",
            "branch --show-current": "feat/160-x\n",
            "log feat/160-x --format=%s -n 10": (
                "feat[160]: do the thing (#160)\nfeat[155]: other\n"
            ),
        }
        first = run_with_git(responses, "--json")
        second = run_with_git(responses, "--json")
        self.assertEqual(first.stdout, second.stdout)
        self.assertEqual(json.loads(first.stdout)["tickets"][0]["key"], "160")


def _real_repo(path: Path, remote: str, subject: str, branch: str = "main") -> Path:
    """A real git checkout with one commit and an `origin` remote (nothing is fetched)."""
    git = ["git", "-c", "user.name=t", "-c", "user.email=t@example.invalid",
           "-c", "commit.gpgsign=false", "-c", "core.hooksPath=" + os.devnull,
           "-c", "init.defaultBranch=" + branch]
    subprocess.run([*git, "init", "-q", str(path)], check=True, capture_output=True)
    subprocess.run([*git, "-C", str(path), "remote", "add", "origin", remote], check=True)
    subprocess.run([*git, "-C", str(path), "commit", "-q", "--allow-empty", "-m", subject],
                   check=True, capture_output=True)
    return path


@unittest.skipUnless(shutil.which("git"), "git is not on PATH")
class RepoRootTests(unittest.TestCase):
    """Issue 182: the scan read whatever checkout the process ran in, so a caller bootstrapping a
    repository from another working directory bound that other checkout's repo and tickets — and
    the output carried nothing that could tell the two apart."""

    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        base = Path(self._tmp.name)
        self.chosen = _real_repo(base / "chosen", "https://github.com/acme/chosen.git",
                                 "fix #12 in the chosen repo")
        self.other = _real_repo(base / "other", "https://github.com/acme/other.git",
                                "fix #99 in the other repo")

    def tearDown(self):
        self._tmp.cleanup()

    def _scan(self, *argv: str) -> dict:
        proc = subprocess.run([sys.executable, "-B", str(SCRIPT), "--json", *argv],
                              capture_output=True, text=True, encoding="utf-8", cwd=str(self.other))
        self.assertEqual(proc.returncode, 0, proc.stderr)
        return json.loads(proc.stdout)

    def test_repo_root_scans_the_named_checkout_not_the_working_directory(self):
        result = self._scan("--repo-root", str(self.chosen))
        self.assertEqual(result["repository"], "acme/chosen")
        self.assertEqual(os.path.realpath(result["root"]), os.path.realpath(self.chosen))
        self.assertEqual([(t["provider"], t["key"]) for t in result["tickets"]], [("github", "12")])

    def test_without_repo_root_the_root_names_the_working_directory(self):
        # The default is unchanged, but the report now says which checkout it read, which is what lets
        # a caller refuse a scan of the wrong repository.
        result = self._scan()
        self.assertEqual(result["repository"], "acme/other")
        self.assertEqual(os.path.realpath(result["root"]), os.path.realpath(self.other))

    def test_a_repo_root_that_is_not_a_checkout_is_unavailable(self):
        proc = subprocess.run(
            [sys.executable, "-B", str(SCRIPT), "--json", "--repo-root",
             str(Path(self._tmp.name) / "missing")],
            capture_output=True, text=True, encoding="utf-8", cwd=str(self.other))
        self.assertEqual(proc.returncode, 2, proc.stderr)
        self.assertIn("git unavailable", proc.stderr)


if __name__ == "__main__":
    unittest.main()
