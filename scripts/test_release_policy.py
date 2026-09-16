import subprocess
import unittest
from unittest.mock import patch

from release_policy import identity, promotion_aliases, remote_refs


REVISION = "a" * 40
ENV = {
    "GITHUB_SHA": REVISION,
    "GITHUB_RUN_ID": "123",
    "GITHUB_RUN_ATTEMPT": "1",
    "GITHUB_EVENT_NAME": "push",
    "GITHUB_REF": "refs/heads/main",
}


class ReleasePolicyTests(unittest.TestCase):
    def test_main_push_aliases(self):
        release = identity(ENV)
        self.assertEqual(["latest", "sha-aaaaaaa"], release["aliases"])
        self.assertEqual("main-aaaaaaa", release["release_version"])

    def test_other_events_and_refs_cannot_publish(self):
        for event in ("pull_request", "pull_request_target", "workflow_dispatch", "release", "push"):
            for ref in ("refs/heads/main", "refs/heads/feature", "refs/tags/v1.2.3", "refs/pull/65/merge"):
                if event == "push" and ref == "refs/heads/main":
                    continue
                with self.subTest(event=event, ref=ref), self.assertRaises(ValueError):
                    identity(ENV | {"GITHUB_EVENT_NAME": event, "GITHUB_REF": ref, "INPUT_VERSION": "1.2.3"})

    def test_candidates_include_full_sha_run_and_attempt(self):
        candidates = {identity(ENV | overrides)["candidate"] for overrides in [
            {}, {"GITHUB_RUN_ID": "124"}, {"GITHUB_RUN_ATTEMPT": "2"},
            {"GITHUB_SHA": "a" * 7 + "b" * 33},
        ]}
        self.assertEqual(4, len(candidates))
        self.assertIn(f"candidate-{REVISION}-123-1", candidates)

    def test_invalid_identity_fails(self):
        for key, value in (("GITHUB_SHA", "bad"), ("GITHUB_RUN_ID", "0"),
                           ("GITHUB_RUN_ATTEMPT", "-1"), ("GITHUB_RUN_ID", "1" * 128)):
            with self.subTest(key=key, value=value), self.assertRaises(ValueError):
                identity(ENV | {key: value})

    def test_stale_main_keeps_only_sha(self):
        release = identity(ENV)
        self.assertEqual(["sha-aaaaaaa"], promotion_aliases(release, REVISION, {"refs/heads/main": "b" * 40}))
        self.assertEqual(release["aliases"], promotion_aliases(release, REVISION, {"refs/heads/main": REVISION}))

    def test_missing_main_fails_closed(self):
        with self.assertRaises(ValueError):
            promotion_aliases(identity(ENV), REVISION, {})

    def test_remote_read_errors_fail_closed(self):
        with patch("release_policy.subprocess.run", side_effect=subprocess.CalledProcessError(128, "git")):
            with self.assertRaises(subprocess.CalledProcessError):
                remote_refs()


if __name__ == "__main__":
    unittest.main()
