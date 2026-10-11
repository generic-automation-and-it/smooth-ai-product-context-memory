"""Shared stub + subprocess scaffolding for the ai-understanding decision-gate harnesses.

Used by `parity_decisions_gate.py` and `gate_behavior.py`. Not part of the shipped skill — it is a
test-only helper in the repository that owns the WET copy.

The gate is a subprocess CLI, never imported for its runtime behaviour; but the parity harness imports
both the copy and the original so it can compare constants and pure functions. Each is imported with
`CONTEXT_MEMORY_CREDENTIAL_FILE` pointed at an absent path first, so neither reads the operator's real
`~/.mimisbrunnr/credentials` at import (issue 182: the gate seeds its settings from that file at
import, and a hermetic suite must not depend on who is running it).
"""

from __future__ import annotations

import http.server
import importlib.util
import json
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import time
from pathlib import Path

REPO = Path(__file__).resolve().parents[4]
AI_SCRIPTS = Path(__file__).resolve().parents[1] / "scripts"
ORIG_SCRIPTS = REPO / ".agents/skills/mimisbrunnr-odin-context-memory/scripts"

GATE = AI_SCRIPTS / "decisions_gate.py"
REDACTOR = AI_SCRIPTS / "redact.py"
RUBRIC = AI_SCRIPTS / "decisions_rubric.json"
ORIG_GATE = ORIG_SCRIPTS / "decisions_gate.py"
ORIG_REDACTOR = ORIG_SCRIPTS / "redact.py"
ORIG_RUBRIC = ORIG_SCRIPTS / "decisions_rubric.json"

HARNESS_CREDENTIAL_FILE = str(Path(tempfile.gettempdir()) / "mimisbrunnr-ai-understanding-harness-cf")

# Import-time seeding: point the credential-file lookup at an absent path before either gate module is
# imported, so its `load_machine_credentials` reads nothing and cannot leak the operator's config.
os.environ["CONTEXT_MEMORY_CREDENTIAL_FILE"] = HARNESS_CREDENTIAL_FILE


def import_module(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


# Import once per harness process. Both are byte-identical on a clean tree; the parity checks exist to
# fail if someone edits the copy and the two diverge. The original is this repo's Mímisbrunnr gate; if
# it is absent (e.g. the skill was ported somewhere without it), the parity harness cannot run and must
# skip rather than crash the whole CI step.
_decisions_gate = import_module("ai_understanding_decisions_gate_copy", GATE)
_orig_gate = import_module("mimisbrunnr_decisions_gate", ORIG_GATE) if ORIG_GATE.is_file() else None


def gate_subprocess_env(env_extra=None):
    """The environment every gate subprocess in these suites runs under.

    Ambient `CONTEXT_MEMORY_DECISIONS_*` is scrubbed and the credential-file lookup is pointed at an
    absent path, so no case inherits the operator's real machine configuration. A case that names its
    own file or settings passes them in `env_extra`.
    """
    env = os.environ.copy()
    for key in list(env):
        if key.startswith("CONTEXT_MEMORY_DECISIONS_"):
            env.pop(key)
    env["CONTEXT_MEMORY_CREDENTIAL_FILE"] = HARNESS_CREDENTIAL_FILE
    env.update(env_extra or {})
    return env


def run_gate(args, stdin_text, env_extra=None, timeout=300, gate=GATE):
    proc = subprocess.run([sys.executable, "-B", str(gate), *args],
                          input=stdin_text, capture_output=True, text=True,
                          encoding="utf-8", env=gate_subprocess_env(env_extra), timeout=timeout)
    return proc


# --------------------------------------------------------------------------- stub


class StubHandler(http.server.BaseHTTPRequestHandler):
    """Answers the decision endpoint with the probabilities the case registered.

    A per-case `override` drives the classified-failure shapes (status, hang, malformed body) so the
    transport assertions are real rather than circular.
    """

    def do_POST(self):  # noqa: N802
        length = int(self.headers.get("Content-Length") or 0)
        raw = self.rfile.read(length) if length else b""
        try:
            body = json.loads(raw.decode("utf-8"))
        except ValueError:
            body = {}

        override = self.server.override
        if override == "hang":
            time.sleep(5)
            return
        if override and override.startswith("status-body:"):
            _, code, text = override.split(":", 2)
            self.send_response(int(code))
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(json.dumps({"error": text}).encode("utf-8"))
            return
        if override and override.startswith("status:"):
            code = int(override.split(":", 1)[1])
            self.send_response(code)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(json.dumps({"error": "stub"}).encode("utf-8"))
            return
        if override == "malformed":
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(b"{not json")
            return
        if override == "no-answers":
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(json.dumps({"model": "nimble"}).encode("utf-8"))
            return

        self.server.requests.append(body)
        probabilities = self.server.probabilities
        answers = {}
        for role in body.get("questions", {}):
            answers[role] = {"noul": probabilities.get(role, 0.0)}
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(json.dumps({"answers": answers}).encode("utf-8"))

    def log_message(self, *args):
        pass


class Stub:
    """A loopback stub the gate is pointed at. Records every request body it served."""

    def __init__(self):
        self.server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), StubHandler)
        self.server.probabilities = {}
        self.server.override = None
        self.server.requests = []
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    @property
    def probabilities(self):
        return self.server.probabilities

    @probabilities.setter
    def probabilities(self, value):
        self.server.probabilities = value

    @property
    def requests(self):
        return self.server.requests

    @property
    def override(self):
        return self.server.override

    @override.setter
    def override(self, value):
        self.server.override = value

    @property
    def base_url(self):
        host, port = self.server.server_address[:2]
        return f"http://127.0.0.1:{port}"

    def close(self):
        self.server.shutdown()
        self.server.server_close()
