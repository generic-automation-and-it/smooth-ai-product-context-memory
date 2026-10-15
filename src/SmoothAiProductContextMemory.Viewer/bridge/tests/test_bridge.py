#!/usr/bin/env python3
"""P4a bridge smoke test — NFR-02 (write-token absence) and the queue payload shape.

Standalone runner. No test framework; the assertions are explicit. The bridge
runs against a live AppHost in normal use, but the L0 guarantees — refusing
to load under a write token, and the queue payload — are unit-testable in
isolation. The bridge's only other routes hit the store, which the harness
cannot provide, so the rest is integration.
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[4]
BRIDGE_DIR = REPO_ROOT / "src" / "SmoothAiProductContextMemory.Viewer" / "bridge"
SHARED_SCRIPTS = REPO_ROOT / ".agents" / "skills" / "_mimisbrunnr-shared" / "scripts"


def _stage_bridge():
    """Mirror the Dockerfile: copy the bridge + the shared capture scripts.

    Returns the directory whose PYTHONPATH makes the bridge importable with
    its capture client. Tests do not modify the repo.
    """
    tmp = Path(tempfile.mkdtemp(prefix="bridge-test-"))
    (tmp / "bridge").mkdir()
    (tmp / "capture").mkdir()
    (tmp / "bridge" / "bridge.py").write_bytes((BRIDGE_DIR / "bridge.py").read_bytes())
    for entry in SHARED_SCRIPTS.iterdir():
        if entry.is_file() and entry.suffix == ".py":
            (tmp / "capture" / entry.name).write_bytes(entry.read_bytes())
    return tmp


def _run_bridge_import(extra_env=None):
    """`python3 -c "import bridge"` in a subprocess, with the staged PYTHONPATH.

    Returns the (returncode, stdout, stderr) of the import. The bridge module
    raises SystemExit on a write-token env, so the returncode carries the
    outcome.
    """
    tmp = _stage_bridge()
    env = {
        "PYTHONPATH": str(tmp / "capture") + os.pathsep + str(tmp / "bridge"),
        # Make the capture client fail-closed on missing credentials: an
        # invalid base URL is enough; the bridge's NFR-02 check runs *before*
        # the client makes any HTTP call.
        "CONTEXT_MEMORY_BASE_URL": "http://127.0.0.1:9",
        "CONTEXT_MEMORY_READ_TOKEN": "harness-token",
        "CONTEXT_MEMORY_CREDENTIAL_FILE": os.devnull,
    }
    if extra_env:
        env.update(extra_env)
    proc = subprocess.run(
        [sys.executable, "-c", "import bridge"],
        env=env, capture_output=True, text=True, encoding="utf-8",
    )
    return proc.returncode, proc.stdout, proc.stderr


class WriteTokenGuardTests(unittest.TestCase):
    def test_bridge_loads_when_no_write_token(self):
        code, _out, err = _run_bridge_import()
        self.assertEqual(code, 0, msg=f"bridge refused to load under a clean env: {err!r}")
        self.assertNotIn("refusing to start", err)

    def test_bridge_refuses_with_standard_write_token(self):
        code, _out, err = _run_bridge_import(
            extra_env={"CONTEXT_MEMORY_WRITE_TOKEN": "harness-write-token"},
        )
        self.assertNotEqual(code, 0, msg="bridge must refuse to load with a write token present")
        self.assertIn("NFR-02", err)
        self.assertIn("CONTEXT_MEMORY_WRITE_TOKEN", err)

    def test_bridge_refuses_with_host_write_token(self):
        code, _out, err = _run_bridge_import(
            extra_env={"ApiAccess__WriteToken": "harness-write-token"},
        )
        self.assertNotEqual(code, 0, msg="bridge must refuse the Host's write token spelling too")
        self.assertIn("ApiAccess__WriteToken", err)

    def test_bridge_refuses_with_parameters_write_token(self):
        code, _out, err = _run_bridge_import(
            extra_env={"Parameters__api-write-token": "harness-write-token"},
        )
        self.assertNotEqual(code, 0, msg="bridge must refuse the legacy Parameters spelling too")
        self.assertIn("Parameters__api-write-token", err)

    def test_empty_value_is_not_a_token(self):
        code, _out, err = _run_bridge_import(
            extra_env={"CONTEXT_MEMORY_WRITE_TOKEN": ""},
        )
        self.assertEqual(code, 0, msg=f"empty value must not count as a token: {err!r}")


class QueuePayloadTests(unittest.TestCase):
    """The queue is a `POST /query` with `Status=proposed` and `IncludeProposed=true`.

    The bridge exposes this as a private helper (`_review_queue_payload`). It
    must set the three switches the work-task DoD names — `status`,
    `includeProposed`, `currentOnly: false` (per LADR-10, a reviewer wants
    the full proposed history, not just the latest).
    """

    def test_default_payload(self):
        tmp = _stage_bridge()
        env = {
            "PYTHONPATH": str(tmp / "capture") + os.pathsep + str(tmp / "bridge"),
            "CONTEXT_MEMORY_BASE_URL": "http://127.0.0.1:9",
            "CONTEXT_MEMORY_READ_TOKEN": "harness-token",
            "CONTEXT_MEMORY_CREDENTIAL_FILE": os.devnull,
        }
        proc = subprocess.run(
            [sys.executable, "-c", (
                "import bridge, json; "
                "print(json.dumps(bridge._review_queue_payload(500)))"
            )],
            env=env, capture_output=True, text=True, encoding="utf-8",
        )
        self.assertEqual(proc.returncode, 0, msg=proc.stderr)
        payload = json.loads(proc.stdout)
        self.assertEqual(payload["status"], "proposed")
        self.assertTrue(payload["includeProposed"])
        self.assertFalse(payload["currentOnly"])
        self.assertEqual(payload["limit"], 500)


if __name__ == "__main__":
    unittest.main(verbosity=2)
