#!/usr/bin/env python3
"""
Skill: agile-github-task-from-diff
Regression tests for create_github_task_from_diff.py (sub-issue linking, label pre-check).

Stdlib `unittest` only. Run directly (`unittest discover` fails — `.agents` is not an
importable path segment):

  python3 .agents/skills/agile-github-task-from-diff/scripts/test_create_github_task_from_diff.py

Deliberately not wired into CI — skills are not production code. `run`/`run_json` are
patched on the loaded module, so no test here shells out to git or gh.
"""
import importlib.util
import io
import unittest
import unittest.mock as mock
from contextlib import redirect_stderr
from pathlib import Path

SCRIPT = Path(__file__).resolve().parent / "create_github_task_from_diff.py"


def load():
    spec = importlib.util.spec_from_file_location("create_github_task_from_diff", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


task = load()


def gh_failure(stderr):
    return task.subprocess.CalledProcessError(1, ["gh"], output="", stderr=stderr)


class LinkSubIssueTests(unittest.TestCase):
    def test_posts_database_id_integer_typed(self):
        with mock.patch.object(task, "run_json", return_value={"id": 987654321, "number": 7}) as run_json, \
                mock.patch.object(task, "run", return_value="") as run:
            self.assertTrue(task.link_sub_issue("o", "r", 42, 7))

        run_json.assert_called_once_with(["gh", "api", "/repos/o/r/issues/7"])
        argv = run.call_args.args[0]
        self.assertIn("/repos/o/r/issues/42/sub_issues", argv)
        self.assertEqual(argv[argv.index("-F") + 1], "sub_issue_id=987654321")
        self.assertNotIn("-f", argv)

    def test_post_failure_returns_false_and_prints_gh_stderr(self):
        err = io.StringIO()
        with mock.patch.object(task, "run_json", return_value={"id": 1}), \
                mock.patch.object(task, "run", side_effect=gh_failure("gh: Validation Failed (HTTP 422)")), \
                redirect_stderr(err):
            self.assertFalse(task.link_sub_issue("o", "r", 42, 7))
        self.assertIn("HTTP 422", err.getvalue())

    def test_id_lookup_failure_returns_false(self):
        err = io.StringIO()
        with mock.patch.object(task, "run_json", side_effect=gh_failure("gh: Not Found (HTTP 404)")), \
                mock.patch.object(task, "run") as run, \
                redirect_stderr(err):
            self.assertFalse(task.link_sub_issue("o", "r", 42, 7))
        run.assert_not_called()
        self.assertIn("HTTP 404", err.getvalue())


class CheckLabelTests(unittest.TestCase):
    def test_present(self):
        with mock.patch.object(task, "run", return_value="{}") as run:
            self.assertEqual(task.check_label("o", "r", "task"), ("present", ""))
        run.assert_called_once_with(["gh", "api", "/repos/o/r/labels/task"])

    def test_404_is_missing(self):
        with mock.patch.object(task, "run", side_effect=gh_failure("gh: Not Found (HTTP 404)")):
            self.assertEqual(task.check_label("o", "r", "task")[0], "missing")

    def test_other_error_is_unknown(self):
        with mock.patch.object(task, "run", side_effect=gh_failure("gh: Bad credentials (HTTP 401)")):
            state, detail = task.check_label("o", "r", "task")
        self.assertEqual(state, "unknown")
        self.assertIn("HTTP 401", detail)

    def test_label_name_is_url_encoded(self):
        with mock.patch.object(task, "run", return_value="{}") as run:
            task.check_label("o", "r", "good first/issue")
        self.assertEqual(run.call_args.args[0][-1], "/repos/o/r/labels/good%20first%2Fissue")

    def test_non_gh_error_is_unknown(self):
        with mock.patch.object(task, "run", side_effect=FileNotFoundError("gh")):
            self.assertEqual(task.check_label("o", "r", "task")[0], "unknown")


class BuildCreateCmdTests(unittest.TestCase):
    def test_label_applied_when_given(self):
        argv = task.build_create_cmd("o", "r", "T", "B", "task")
        self.assertEqual(argv[-2:], ["--label", "task"])

    def test_no_label_flag_when_label_dropped(self):
        self.assertNotIn("--label", task.build_create_cmd("o", "r", "T", "B", None))


class NoParentIdTests(unittest.TestCase):
    def test_flag_defaults_off(self):
        self.assertFalse(task.parse_args([]).noparentid)

    def test_flag_parses(self):
        self.assertTrue(task.parse_args(["--noparentid"]).noparentid)

    def test_combined_with_feature_issue_errors(self):
        err = io.StringIO()
        with redirect_stderr(err), self.assertRaises(SystemExit) as exit_:
            task.parse_args(["--noparentid", "--feature-issue", "42"])
        self.assertEqual(exit_.exception.code, 2)
        self.assertIn("--noparentid cannot be combined with --feature-issue", err.getvalue())


class BuildTaskBodyTests(unittest.TestCase):
    def body(self, feature_issue):
        return task.build_task_body(
            "b", "origin/main", "abcdef0", ["README.md"], {"M": 1}, "stat", ["documentation"], feature_issue
        )

    def test_without_parent_scopes_against_repo(self):
        body = self.body(None)
        self.assertIn("scope confirmed against the repo.", body)
        self.assertNotIn("sub-issue", body)

    def test_with_parent_scopes_against_feature(self):
        body = self.body(42)
        self.assertIn("scope confirmed against parent Feature #42.", body)
        self.assertIn("sub-issue of Feature #42", body)


if __name__ == "__main__":
    unittest.main()
