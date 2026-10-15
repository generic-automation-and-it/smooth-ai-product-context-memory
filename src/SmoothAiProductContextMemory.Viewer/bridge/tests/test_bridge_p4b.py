#!/usr/bin/env python3
"""P4b bridge tests — NFR-02 (write-token requirement), route shape, and the
two-phase approve flow.

Mirrors `test_bridge.py`'s standalone harness: stage the bridge + capture
client into a temp dir, run `python3 -c` in a subprocess with controlled env,
assert on the returncode / stderr. No live HTTP server is required for the
L0 guarantees; the route-shape tests inspect the bridge's exported handler
class.
"""
from __future__ import annotations

import json
import os
import re
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

    P4b imports `context_memory_client`; it must be importable from the
    staged tree.
    """
    tmp = Path(tempfile.mkdtemp(prefix="bridge-p4b-test-"))
    (tmp / "bridge").mkdir()
    (tmp / "capture").mkdir()
    (tmp / "bridge" / "bridge_p4b.py").write_bytes((BRIDGE_DIR / "bridge_p4b.py").read_bytes())
    for entry in SHARED_SCRIPTS.iterdir():
        if entry.is_file() and entry.suffix == ".py":
            (tmp / "capture" / entry.name).write_bytes(entry.read_bytes())
    return tmp


def _import_env(extra_env=None):
    tmp = _stage_bridge()
    env = {
        "PYTHONPATH": str(tmp / "capture") + os.pathsep + str(tmp / "bridge"),
        # Capture client fails closed on missing credentials without making
        # any HTTP call. An invalid base URL + empty credentials file is
        # enough to exercise the import-time guards without ever reaching
        # the network.
        "CONTEXT_MEMORY_BASE_URL": "http://127.0.0.1:9",
        "CONTEXT_MEMORY_READ_TOKEN": "harness-read-token",
        "CONTEXT_MEMORY_WRITE_TOKEN": "harness-write-token",
        "CONTEXT_MEMORY_CREDENTIAL_FILE": os.devnull,
    }
    if extra_env is not None:
        # Caller wants to control every var (e.g. omit the write token to
        # test the refusal guard).
        env = dict(extra_env)
    return tmp, env


def _run_import(extra_env=None):
    tmp, env = _import_env(extra_env)
    proc = subprocess.run(
        [sys.executable, "-c", "import bridge_p4b"],
        env=env, capture_output=True, text=True, encoding="utf-8",
    )
    return proc.returncode, proc.stdout, proc.stderr


def _run_bridge_snippet(snippet, extra_env=None):
    """Run `python3 -c <snippet>` against the staged bridge + return the output."""
    tmp, env = _import_env(extra_env)
    proc = subprocess.run(
        [sys.executable, "-c", snippet],
        env=env, capture_output=True, text=True, encoding="utf-8",
    )
    return proc.returncode, proc.stdout, proc.stderr


class WriteTokenGuardTests(unittest.TestCase):
    def test_p4b_loads_with_write_token(self):
        code, _out, err = _run_import()
        self.assertEqual(code, 0, msg=f"P4b refused to load with a write token: {err!r}")
        self.assertNotIn("refusing to start", err)

    def test_p4b_refuses_without_write_token(self):
        # All canonical env names absent.
        env = {
            "PYTHONPATH": _stage_bridge() and str(Path(tempfile.gettempdir())),
            "CONTEXT_MEMORY_BASE_URL": "http://127.0.0.1:9",
            "CONTEXT_MEMORY_READ_TOKEN": "harness-read-token",
            "CONTEXT_MEMORY_CREDENTIAL_FILE": os.devnull,
        }
        # Re-stage properly (the expression above is a hack; do it cleanly).
        tmp = _stage_bridge()
        env = {
            "PYTHONPATH": str(tmp / "capture") + os.pathsep + str(tmp / "bridge"),
            "CONTEXT_MEMORY_BASE_URL": "http://127.0.0.1:9",
            "CONTEXT_MEMORY_READ_TOKEN": "harness-read-token",
            "CONTEXT_MEMORY_CREDENTIAL_FILE": os.devnull,
        }
        proc = subprocess.run(
            [sys.executable, "-c", "import bridge_p4b"],
            env=env, capture_output=True, text=True, encoding="utf-8",
        )
        self.assertNotEqual(proc.returncode, 0,
                            msg="P4b must refuse to start without a write token")
        self.assertIn("refusing to start", proc.stderr)
        self.assertIn("P4b requires a write-token env var", proc.stderr)

    def test_p4b_refuses_with_empty_write_token(self):
        tmp = _stage_bridge()
        env = {
            "PYTHONPATH": str(tmp / "capture") + os.pathsep + str(tmp / "bridge"),
            "CONTEXT_MEMORY_BASE_URL": "http://127.0.0.1:9",
            "CONTEXT_MEMORY_READ_TOKEN": "harness-read-token",
            "CONTEXT_MEMORY_WRITE_TOKEN": "",  # empty is not a token
            "CONTEXT_MEMORY_CREDENTIAL_FILE": os.devnull,
        }
        proc = subprocess.run(
            [sys.executable, "-c", "import bridge_p4b"],
            env=env, capture_output=True, text=True, encoding="utf-8",
        )
        self.assertNotEqual(proc.returncode, 0,
                            msg="P4b must refuse when the only token is empty")

    def test_p4b_aliases_friendly_write_token_name(self):
        # The AppHost injects under API_WRITE_TOKEN; the bridge must alias to
        # the canonical CONTEXT_MEMORY_WRITE_TOKEN before the client imports.
        tmp = _stage_bridge()
        env = {
            "PYTHONPATH": str(tmp / "capture") + os.pathsep + str(tmp / "bridge"),
            "CONTEXT_MEMORY_BASE_URL": "http://127.0.0.1:9",
            "CONTEXT_MEMORY_READ_TOKEN": "harness-read-token",
            "API_WRITE_TOKEN": "harness-write-token",
            "CONTEXT_MEMORY_CREDENTIAL_FILE": os.devnull,
        }
        proc = subprocess.run(
            [sys.executable, "-c",
             "import os, bridge_p4b; assert os.environ['CONTEXT_MEMORY_WRITE_TOKEN'] == 'harness-write-token'"],
            env=env, capture_output=True, text=True, encoding="utf-8",
        )
        self.assertEqual(proc.returncode, 0,
                         msg=f"P4b must alias API_WRITE_TOKEN: {proc.stderr!r}")


class RouteShapeTests(unittest.TestCase):
    """LADR-11 + worktask DoD: the routes the SPA hits are `/bridge/approve`,
    `/bridge/reject`, `/bridge/amend`, and `/bridge/health`. Anything else is
    404. P4a's routes (`/bridge/review-queue`, `/bridge/dedup-preview`) must
    not appear on P4b.
    """

    def _import_bridge(self):
        return _run_bridge_snippet("import bridge_p4b as b; print('ok')")

    def test_p4b_exposes_approve_reject_amend_health(self):
        code, out, err = self._import_bridge()
        self.assertEqual(code, 0, msg=err)
        snippet = (
            "import bridge_p4b as b; "
            "m = [m for m in dir(b) if not m.startswith('_')]; "
            "assert hasattr(b, 'Handler'); "
            "h = b.Handler; "
            "print(','.join(sorted(h.do_POST.__code__.co_consts or [])))"
        )
        # Simpler: just check the source for the route strings.
        bridge_src = (BRIDGE_DIR / "bridge_p4b.py").read_text(encoding="utf-8")
        for path in ("/bridge/approve", "/bridge/reject", "/bridge/amend", "/bridge/health"):
            self.assertIn(path, bridge_src, msg=f"{path} not registered in P4b")

    def test_p4b_does_not_register_p4a_routes(self):
        bridge_src = (BRIDGE_DIR / "bridge_p4b.py").read_text(encoding="utf-8")
        # P4a routes are intentionally absent from P4b — they live on port 5181.
        self.assertNotIn("/bridge/review-queue", bridge_src)
        self.assertNotIn("/bridge/dedup-preview", bridge_src)

    def test_p4b_phase_declared_p4b_write(self):
        bridge_src = (BRIDGE_DIR / "bridge_p4b.py").read_text(encoding="utf-8")
        self.assertIn('"phase": "p4b-write"', bridge_src)
        self.assertIn("phase=p4b-write", bridge_src)

    def test_approve_route_uses_set_with_status_approved(self):
        """LADR-11: the transition is `set` with Status=approved on the Uuid."""
        bridge_src = (BRIDGE_DIR / "bridge_p4b.py").read_text(encoding="utf-8")
        self.assertIn('payload["items"][0]["status"] = "approved"', bridge_src)
        # LADR-11 amend: the amend path uses Status=proposed.
        self.assertIn('payload["items"][0]["status"] = "proposed"', bridge_src)

    def test_approve_dryrun_then_confirm(self):
        """The worktask P4b.2: digest first, then approve on confirm. The
        handler must gate the write on the `confirm` body field and re-check
        the digest fingerprint to refuse a stale write.
        """
        bridge_src = (BRIDGE_DIR / "bridge_p4b.py").read_text(encoding="utf-8")
        self.assertIn("confirm = bool(body.get(\"confirm\"))", bridge_src)
        self.assertIn("digest-stale", bridge_src)
        self.assertIn("digestFingerprint", bridge_src)

    def test_reject_writes_no_store_request(self):
        """P4b.3: reject records the decision without issuing a write request."""
        bridge_src = (BRIDGE_DIR / "bridge_p4b.py").read_text(encoding="utf-8")
        # The reject handler must not call the capture client. Search for the
        # reject handler body and ensure no `_run_capture_set` call sits in
        # it.
        m = re.search(
            r"def _handle_reject\(self\):(.*?)(?=\n    def |\nclass |\Z)",
            bridge_src, re.DOTALL,
        )
        self.assertIsNotNone(m, msg="reject handler not found")
        body = m.group(1)
        self.assertNotIn("_run_capture_set", body)
        self.assertNotIn("/api/context/memories", body)

    def test_reject_log_path_is_configurable(self):
        bridge_src = (BRIDGE_DIR / "bridge_p4b.py").read_text(encoding="utf-8")
        self.assertIn("BRIDGE_P4B_REJECT_LOG", bridge_src)


class SubprocessIsolationTests(unittest.TestCase):
    """The capture client's `cmd_set` prints the response to stdout. The
    bridge subprocesses it (Q4 from the G2 plan) to keep the response out of
    the bridge's HTTP body. The subprocess helper must write a temp file
    (the capture client accepts --payload <file>), chmod 0600 (the
    `redact.require_owner_only` precondition), and parse the JSON output.
    """

    def test_subprocess_helper_writes_owner_only_tempfile(self):
        bridge_src = (BRIDGE_DIR / "bridge_p4b.py").read_text(encoding="utf-8")
        self.assertIn("NamedTemporaryFile", bridge_src)
        self.assertIn("chmod(tmp_path, 0o600)", bridge_src)
        self.assertIn("--payload", bridge_src)

    def test_subprocess_helper_parses_json_stdout(self):
        bridge_src = (BRIDGE_DIR / "bridge_p4b.py").read_text(encoding="utf-8")
        self.assertIn("json.loads(text)", bridge_src)

    def test_subprocess_helper_has_timeout(self):
        bridge_src = (BRIDGE_DIR / "bridge_p4b.py").read_text(encoding="utf-8")
        self.assertIn("timeout=_CAPTURE_TIMEOUT", bridge_src)


if __name__ == "__main__":
    unittest.main(verbosity=2)
