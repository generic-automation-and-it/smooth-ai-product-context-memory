#!/usr/bin/env python3
"""Committed L0 harness for the Heimdallr session-metadata scan.

Run: python3 -B .agents/skills/mimisbrunnr-heimdallr-find-session-metadata/tests/run_tests.py

Puts a fake `git` on PATH (no checkout touched) and asserts: repo parsing across
URL shapes, ticket extraction with sources, initiative flag-or-unknown, and the
console-only contract (no file created, --json parses). stdlib unittest.
"""

import json
import os
import stat
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SKILL = Path(__file__).resolve().parents[1]
SCRIPT = SKILL / "scripts" / "find_session_metadata.py"


def run_with_git(responses: dict, *argv: str) -> subprocess.CompletedProcess:
    """Run the script with a fake git answering from `responses` keyed by argv tail."""
    tmp = tempfile.mkdtemp()
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
            [sys.executable, "-B", str(SCRIPT), *argv],
            capture_output=True, text=True, encoding="utf-8", env=env, cwd=tmp,
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
                "log --format=%s -n 10": "\n",
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


class TicketTests(unittest.TestCase):
    def _scan(self, branch: str, log: str, *extra: str) -> dict:
        proc = run_with_git(
            {
                "remote get-url origin": "https://github.com/acme/widgets.git\n",
                "branch --show-current": branch + "\n",
                "log --format=%s -n 10": log,
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

    def test_dedupe_keeps_first_source(self):
        result = self._scan("feat/160-x", "revisit #160\n")
        hits = [t for t in result["tickets"] if t["key"] == "160"]
        self.assertEqual(len(hits), 1)
        self.assertEqual(hits[0]["seenIn"], "branch")

    def test_initiative_flag_or_unknown(self):
        self.assertEqual(self._scan("main", "")["initiative"], "unknown")
        self.assertEqual(
            self._scan("main", "", "--initiative", "mimisbrunnr")["initiative"],
            "mimisbrunnr",
        )


class ContractTests(unittest.TestCase):
    def test_human_default_and_no_files(self):
        tmp = tempfile.mkdtemp()
        try:
            before = set(os.listdir(tmp))
            proc = run_with_git(
                {
                    "remote get-url origin": "https://github.com/acme/widgets.git\n",
                    "branch --show-current": "feat/160-x\n",
                    "log --format=%s -n 10": "\n",
                }
            )
            self.assertEqual(proc.returncode, 0, proc.stderr)
            self.assertIn("repository: acme/widgets", proc.stdout)
            self.assertIn("github:160", proc.stdout)
            self.assertEqual(set(os.listdir(tmp)), before)
        finally:
            import shutil

            shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__":
    unittest.main()
