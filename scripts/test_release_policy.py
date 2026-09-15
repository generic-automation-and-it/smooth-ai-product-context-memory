import unittest
from unittest.mock import patch

from release_policy import check_target, check_version_images, identity, promotion_aliases, remote_refs, version_parts


REVISION = "a" * 40
ENV = {
    "GITHUB_SHA": REVISION,
    "GITHUB_RUN_ID": "123",
    "GITHUB_RUN_ATTEMPT": "1",
    "GITHUB_EVENT_NAME": "push",
    "GITHUB_REF": "refs/heads/main",
}


class ReleasePolicyTests(unittest.TestCase):
    def test_version_images_cannot_be_replaced_or_unverified(self):
        import subprocess
        cases = [
            (0, "sha256:abc\n", "", True),
            (0, "sha256:other\n", "", False),
            (1, "", "ERROR: example/api:1.2.3: not found", True),
            (1, "", "manifest unknown", True),
            (1, "", "unauthorized", False),
            (1, "", "network timeout", False),
        ]
        for code, out, error, allowed in cases:
            with self.subTest(error=error, out=out), patch(
                "release_policy.subprocess.run",
                return_value=subprocess.CompletedProcess([], code, out, error),
            ):
                if allowed:
                    check_version_images("1.2.3", [("example/api", "sha256:abc")])
                else:
                    with self.assertRaises(ValueError):
                        check_version_images("1.2.3", [("example/api", "sha256:abc")])

    def test_event_alias_matrix(self):
        cases = [
            ({}, ["latest", "sha-aaaaaaa"], False),
            ({"GITHUB_REF": "refs/tags/v1.2.3"}, ["1.2.3", "1.2", "sha-aaaaaaa"], False),
            ({"GITHUB_REF": "refs/tags/v1.2.3-rc.1"}, ["1.2.3-rc.1", "sha-aaaaaaa"], True),
            ({"GITHUB_EVENT_NAME": "workflow_dispatch", "INPUT_VERSION": "1.2.3"},
             ["1.2.3", "sha-aaaaaaa"], False),
            ({"GITHUB_EVENT_NAME": "workflow_dispatch", "INPUT_VERSION": "1.2.3-rc.1"},
             ["1.2.3-rc.1", "sha-aaaaaaa"], True),
        ]
        for overrides, aliases, prerelease in cases:
            with self.subTest(overrides=overrides):
                release = identity(ENV | overrides)
                self.assertEqual(aliases, release["aliases"])
                self.assertEqual(prerelease, release["prerelease"])
                self.assertEqual(aliases[0] != "latest", release["create_release"])

    def test_invalid_versions_fail_for_dispatch_and_tags(self):
        for version in ["", " ", "latest", "v1.2.3", "01.2.3", "1.02.3", "1.2.03",
                        "1.2.3-01", "1.2.3-rc.01", "1.2.3-", "1.2.3-a..b", "1.2.3-.a",
                        "1.2.3-a.", "1.2.3+build", "1.2.3-a_b", "1.2.3\n", "1.2.3-\u00e9",
                        "1.2.3-" + "a" * 123, "1.2.3\nmalicious=output"]:
            for event in ("workflow_dispatch", "push"):
                with self.subTest(version=version, event=event), self.assertRaises(ValueError):
                    identity(ENV | {"GITHUB_EVENT_NAME": event, "GITHUB_REF": f"refs/tags/v{version}",
                                    "INPUT_VERSION": version})

    def test_valid_semver_boundaries(self):
        for version in ["0.0.0", "1.2.3-0", "1.2.3-00a", "1.2.3--", "1.2.3-alpha-1.0",
                        "1.2.3-" + "a" * 122]:
            with self.subTest(version=version):
                version_parts(version)

    def test_unsupported_events_fail(self):
        for overrides in [{"GITHUB_EVENT_NAME": "pull_request"}, {"GITHUB_REF": "refs/heads/feature"},
                          {"GITHUB_REF": "refs/tags/1.2.3"}]:
            with self.assertRaises(ValueError):
                identity(ENV | overrides)

    def test_candidates_include_full_sha_run_and_attempt(self):
        candidates = {identity(ENV | overrides)["candidate"] for overrides in [
            {}, {"GITHUB_RUN_ID": "124"}, {"GITHUB_RUN_ATTEMPT": "2"},
            {"GITHUB_SHA": "a" * 7 + "b" * 33},
        ]}
        self.assertEqual(4, len(candidates))
        self.assertIn(f"candidate-{REVISION}-123-1", candidates)

    def test_lightweight_and_annotated_tag_targets(self):
        release = identity(ENV | {"GITHUB_REF": "refs/tags/v1.2.3"})
        ref = "refs/tags/v1.2.3"
        self.assertFalse(check_target(release, REVISION, {}))
        self.assertTrue(check_target(release, REVISION, {ref: REVISION}))
        self.assertTrue(check_target(release, REVISION, {ref: "b" * 40, ref + "^{}": REVISION}))
        for refs in [{ref: "b" * 40}, {ref: REVISION, ref + "^{}": "b" * 40}]:
            with self.assertRaises(ValueError):
                promotion_aliases(release, REVISION, refs)
        with self.assertRaises(ValueError):
            promotion_aliases(release, REVISION, {})

    def test_stale_main_keeps_only_sha(self):
        release = identity(ENV)
        for refs in [{}, {"refs/heads/main": "b" * 40}]:
            self.assertEqual(["sha-aaaaaaa"], promotion_aliases(release, REVISION, refs))
        self.assertEqual(release["aliases"], promotion_aliases(release, REVISION, {"refs/heads/main": REVISION}))

    def test_stable_lane_never_regresses(self):
        release = identity(ENV | {"GITHUB_REF": "refs/tags/v1.2.9"})
        refs = {"refs/tags/v1.2.9": REVISION}
        for version, expected in [("1.2.10", False), ("1.2.8", True), ("1.3.0", True),
                                  ("1.2.10-rc.1", True), ("01.2.10", True)]:
            with self.subTest(version=version):
                aliases = promotion_aliases(release, REVISION, refs | {f"refs/tags/v{version}": "b" * 40})
                self.assertEqual(expected, "1.2" in aliases)
                self.assertIn("1.2.9", aliases)

    def test_remote_read_errors_fail_closed(self):
        import subprocess
        with patch("release_policy.subprocess.run", side_effect=subprocess.CalledProcessError(128, "git")):
            with self.assertRaises(subprocess.CalledProcessError):
                remote_refs()


if __name__ == "__main__":
    unittest.main()
