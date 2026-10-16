# Mímisbrunnr Console bridge — Phase P4b (write path: approve, reject, amend).
#
# Companion to bridge.py (P4a, read-side). Same image, different process. The
# write token is no longer ambient env: the Console session supplies it per
# request, and this bridge forwards it to the Host, which enforces the
# capability. P4a still refuses any write-token env; this bridge has none either
# (NFR-02: write is never ambient in the image).
#
# Routes:
#   GET  /bridge/p4b-health        liveness; declares p4b-write phase + per-request auth mode
#   POST /bridge/approve           two-phase: dryrun digest, then confirm + write
#   POST /bridge/reject            record the decision; no write
#   POST /bridge/amend             re-send the proposal with Status=proposed on existing Uuid
#
# LADR-11: the transition is a `set` version-bump with Status=approved; v1
# superseded by v2. The bridge re-builds the MemoryWrite from the stored
# proposed (and any post-amend content), drives it through the capture
# pipeline's `set` invocation. The `set --dryrun` digest is the human veto
# point (P4b.2: digest, then approve). Approve re-runs the full gate sequence
# (redaction → atomicity → dedup → `set --dryrun` → write) before writing
# approved (NFR-03).
#
# Subprocess isolation: the capture client's `cmd_set` `print`s the response
# to stdout, so a library-call reuse from this process would contaminate the
# HTTP body the bridge is about to write. Subprocess the capture client per
# write — the same pattern `_fetch_blob` uses for read paths.
import hashlib
import json
import os
import subprocess
import sys
import tempfile
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

HOST = "127.0.0.1"
PORT = 5182

# Env-name aliasing: the AppHost injects the read token and Host base URL under
# friendly names; the capture client reads them under the canonical names. The
# write token is never ambient (NFR-02) — it is supplied per request.
_READ_TOKEN_NAME = "CONTEXT_MEMORY_READ_TOKEN"
_BASE_URL_NAME = "CONTEXT_MEMORY_BASE_URL"
_READ_TOKEN_ALIAS = "API_READ_TOKEN"
_BASE_URL_ALIAS = "HOST_BASE_URL"

_CAPTURE_TIMEOUT = 30
_REJECT_LOG_PATH = os.environ.get("BRIDGE_P4B_REJECT_LOG", "/tmp/bridge_p4b_rejects.log")


def _alias_env():
    """Map the AppHost's env names onto the capture client's canonical names.

    The read token and Host base URL are aliased before the client imports so
    it sees its expected names. The write token is never aliased here — it is
    supplied per request (NFR-02) and injected into the subprocess env.
    """
    if _READ_TOKEN_ALIAS in os.environ and _READ_TOKEN_NAME not in os.environ:
        os.environ[_READ_TOKEN_NAME] = os.environ[_READ_TOKEN_ALIAS]
    if _BASE_URL_ALIAS in os.environ and _BASE_URL_NAME not in os.environ:
        os.environ[_BASE_URL_NAME] = os.environ[_BASE_URL_ALIAS]


_alias_env()

# Import after aliasing. The capture client's import-time `load_machine_credentials`
# seeds the read token from the machine credential file; the write token is never
# ambient (NFR-02) and arrives per request.
import context_memory_client as client  # noqa: E402


def _log(line):
    # Minimal, structured, never a value. stderr so stdout stays parseable JSON
    # for any subprocess we run.
    print(f"[bridge-p4b] {line}", file=sys.stderr)


def _capture_client_path():
    """Locate the capture client entry script.

    In the image the shared scripts live at `/bridge/capture/` (Dockerfile
    copies them; PYTHONPATH set). In the test harness they are staged by
    `_stage_bridge` to a temp dir.
    """
    candidates = [
        "/bridge/capture/context_memory_client.py",
        os.path.join(os.path.dirname(__file__), "capture", "context_memory_client.py"),
    ]
    for path in candidates:
        if os.path.exists(path):
            return path
    raise RuntimeError("capture client not found in any expected path")


def _strip_banner(text):
    """The capture client prepends a recall-notice banner to framed output.

    `cmd_set` is not in `FRAMED_COMMANDS` (only read commands frame), so its
    stdout is the raw JSON dump. No banner to strip — kept as a defensive
    hook for the future when framing lands on write commands.
    """
    return text


def _run_capture_set(payload, dryrun, token=None):
    """Subprocess the capture client's `set` and return its JSON response.

    The capture client reads the payload from `--payload <file>`. The file is
    written with owner-only permissions (0600) to satisfy
    `redact.require_owner_only`. `--consume` deletes the file after read.
    The write credential is the session's bearer token, injected as env for the
    subprocess only (never the bridge's own env).
    """
    argv = [sys.executable, "-B", _capture_client_path(), "set",
            "--payload", "-", "--consume"]
    if dryrun:
        argv.append("--dryrun")
    with tempfile.NamedTemporaryFile(
        mode="w", suffix=".json", prefix="bridge-p4b-",
        delete=False, encoding="utf-8",
    ) as tmp:
        json.dump(payload, tmp)
        tmp_path = tmp.name
    os.chmod(tmp_path, 0o600)
    env = {**os.environ, client.ENV_WRITE_TOKEN: token} if token else None
    try:
        proc = subprocess.run(
            argv + ["--payload", tmp_path],  # safer than /dev/stdin on some platforms
            capture_output=True, text=True, encoding="utf-8", timeout=_CAPTURE_TIMEOUT,
            env=env,
        )
    finally:
        try:
            os.unlink(tmp_path)
        except OSError:
            pass
    if proc.returncode != 0:
        raise RuntimeError(
            f"capture set failed: rc={proc.returncode} stderr={proc.stderr[:200]!r}"
        )
    text = _strip_banner(proc.stdout)
    try:
        return json.loads(text)
    except ValueError as exc:
        raise RuntimeError(f"capture set returned non-JSON: {exc}: {text[:200]!r}")


def _fingerprint(digest):
    """Stable hash of a dryrun response for the confirm-step stale check."""
    return hashlib.sha256(
        json.dumps(digest, sort_keys=True).encode("utf-8")
    ).hexdigest()


def _fetch_current_version(uuid, scope, token=None):
    """Read the current version of a memory (cheap fields).

    Uses `client._request` directly — the library function `cmd_get_versions`
    `print`s its response, which would contaminate the HTTP body. The P4a
    pattern, used here for the same reason.
    """
    query = {"scope": scope} if scope else None
    resp = client._request("GET", f"/api/context/memories/{uuid}", query=query, token=token)
    if not isinstance(resp, dict):
        raise ValueError("memory read returned non-object response")
    return resp


def _build_set_payload(memory, group_uuid, body_overrides=None):
    """Construct a `set` payload from the stored current version of a memory.

    Field selection is the minimum the Host's `SetMemories` validator +
    handler needs to recognise the proposed memory and apply the transition.
    `body_overrides` is a partial `MemoryWrite`-shaped dict; non-null fields
    replace the corresponding stored value (used by `/bridge/amend`).
    """
    item = {
        "uuid": memory.get("uuid"),
        "name": memory.get("name"),
        "description": memory.get("description"),
        "statement": memory.get("statement") or "",
        "contentSummary": memory.get("contentSummary") or "",
        "kind": memory.get("kind"),
        "facets": memory.get("facets"),
        "tags": memory.get("tags"),
        "status": memory.get("status"),
        "confidence": memory.get("confidence") or 80,
        "sources": memory.get("sources"),
        "validFrom": memory.get("validFrom"),
        "validUntil": memory.get("validUntil"),
    }
    if body_overrides:
        for key, value in body_overrides.items():
            if value is not None:
                item[key] = value
    return {"groupUuid": group_uuid, "items": [item]}


class Handler(BaseHTTPRequestHandler):
    def _reply(self, status, payload):
        body = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _read_json(self):
        length = int(self.headers.get("Content-Length") or 0)
        if length <= 0:
            return {}
        raw = self.rfile.read(length)
        try:
            data = json.loads(raw)
        except ValueError:
            self._reply(400, {"error": "bad-input", "detail": "request body was not valid JSON"})
            return None
        if not isinstance(data, dict):
            self._reply(400, {"error": "bad-input", "detail": "request body must be a JSON object"})
            return None
        return data

    def _bearer(self):
        auth = self.headers.get("Authorization") or ""
        if auth.startswith("Bearer "):
            return auth[len("Bearer "):].strip()
        return ""

    def do_GET(self):
        if self.path == "/bridge/p4b-health":
            self._reply(200, {
                "status": "ok",
                "phase": "p4b-write",
                "routes": ["/bridge/p4b-health", "/bridge/approve", "/bridge/reject", "/bridge/amend"],
                "authMode": "per-request",
            })
            return
        self._reply(404, {"error": "not-found", "route": self.path})

    def do_POST(self):
        if self.path == "/bridge/approve":
            self._handle_approve()
            return
        if self.path == "/bridge/reject":
            self._handle_reject()
            return
        if self.path == "/bridge/amend":
            self._handle_amend()
            return
        self._reply(404, {"error": "not-found", "route": self.path})

    def _handle_approve(self):
        body = self._read_json()
        if body is None:
            return
        token = self._bearer()
        if not token:
            self._reply(401, {"error": "unauthorized", "detail": "missing bearer token"})
            return
        uuid = body.get("uuid")
        scope = body.get("scope") or ""
        confirm = bool(body.get("confirm"))
        digest_fingerprint = body.get("digestFingerprint")
        if not isinstance(uuid, str) or not uuid.strip():
            self._reply(400, {"error": "bad-input", "detail": "uuid is required"})
            return
        overrides = body.get("overrides") or {}
        if not isinstance(overrides, dict):
            self._reply(400, {"error": "bad-input", "detail": "overrides must be an object"})
            return
        try:
            memory = _fetch_current_version(uuid.strip(), scope, token)
        except (client.ClientError, OSError, ValueError) as exc:
            _log(f"approve read: refused ({type(exc).__name__})")
            self._reply(502, {"error": "store-refused", "detail": type(exc).__name__})
            return
        group_uuid = memory.get("groupUuid")
        if not group_uuid:
            self._reply(502, {"error": "memory-missing-group", "detail": "memory had no groupUuid"})
            return
        # LADR-11: the transition is `set` with Status=approved on the existing Uuid.
        payload = _build_set_payload(memory, group_uuid, overrides)
        payload["items"][0]["status"] = "approved"
        try:
            dryrun_resp = _run_capture_set(payload, dryrun=True, token=token)
        except (RuntimeError, subprocess.TimeoutExpired) as exc:
            _log(f"approve dryrun: refused ({type(exc).__name__})")
            self._reply(503, {"error": "capture-failed", "detail": type(exc).__name__})
            return
        if not confirm:
            fp = _fingerprint(dryrun_resp)
            _log(f"approve digest: {uuid}")
            self._reply(200, {
                "status": "digest",
                "uuid": uuid,
                "digest": dryrun_resp,
                "digestFingerprint": fp,
            })
            return
        # Confirm step: re-run dryrun, compare to the fingerprint the SPA acknowledged.
        # A mismatch means the proposal changed between digest and confirm (another
        # amend, an automated export, etc.) — refuse and let the SPA re-confirm.
        # F1 (P4b.2 veto): a confirm without the digest fingerprint would skip the
        # stale check and write a proposal the human never saw the digest for. The
        # fingerprint is the veto receipt; it is mandatory on confirm.
        if not digest_fingerprint:
            self._reply(400, {
                "error": "bad-input",
                "detail": "digestFingerprint is required when confirm is true",
            })
            return
        current_fp = _fingerprint(dryrun_resp)
        if current_fp != digest_fingerprint:
            _log(f"approve confirm: stale digest ({uuid})")
            self._reply(409, {
                "error": "digest-stale",
                "detail": "the proposal changed between digest and confirm; please re-confirm",
                "currentDigestFingerprint": current_fp,
            })
            return
        try:
            written = _run_capture_set(payload, dryrun=False, token=token)
        except (RuntimeError, subprocess.TimeoutExpired) as exc:
            _log(f"approve write: refused ({type(exc).__name__})")
            self._reply(503, {"error": "write-failed", "detail": type(exc).__name__})
            return
        _log(f"approve write: {uuid}")
        self._reply(200, {"status": "written", "uuid": uuid, "response": written})

    def _handle_reject(self):
        body = self._read_json()
        if body is None:
            return
        token = self._bearer()
        if not token:
            self._reply(401, {"error": "unauthorized", "detail": "missing bearer token"})
            return
        uuid = body.get("uuid")
        reason = body.get("reason") or ""
        if not isinstance(uuid, str) or not uuid.strip():
            self._reply(400, {"error": "bad-input", "detail": "uuid is required"})
            return
        decision = {
            "uuid": uuid.strip(),
            "reason": reason,
        }
        try:
            with open(_REJECT_LOG_PATH, "a", encoding="utf-8") as f:
                f.write(json.dumps(decision) + "\n")
        except OSError as exc:
            _log(f"reject: log write failed ({exc})")
            self._reply(503, {"error": "reject-log-failed", "detail": str(exc)})
            return
        _log(f"reject: {uuid}")
        self._reply(200, {"status": "rejected", "uuid": uuid})

    def _handle_amend(self):
        body = self._read_json()
        if body is None:
            return
        token = self._bearer()
        if not token:
            self._reply(401, {"error": "unauthorized", "detail": "missing bearer token"})
            return
        uuid = body.get("uuid")
        scope = body.get("scope") or ""
        overrides = body.get("overrides") or {}
        if not isinstance(uuid, str) or not uuid.strip():
            self._reply(400, {"error": "bad-input", "detail": "uuid is required"})
            return
        if not isinstance(overrides, dict):
            self._reply(400, {"error": "bad-input", "detail": "overrides must be an object"})
            return
        if not overrides:
            self._reply(400, {"error": "bad-input", "detail": "overrides is empty: nothing to amend"})
            return
        try:
            memory = _fetch_current_version(uuid.strip(), scope, token)
        except (client.ClientError, OSError, ValueError) as exc:
            _log(f"amend read: refused ({type(exc).__name__})")
            self._reply(502, {"error": "store-refused", "detail": type(exc).__name__})
            return
        group_uuid = memory.get("groupUuid")
        if not group_uuid:
            self._reply(502, {"error": "memory-missing-group", "detail": "memory had no groupUuid"})
            return
        # LADR-11: amend is a version-bump with Status=proposed on the same Uuid.
        payload = _build_set_payload(memory, group_uuid, overrides)
        payload["items"][0]["status"] = "proposed"
        try:
            written = _run_capture_set(payload, dryrun=False, token=token)
        except (RuntimeError, subprocess.TimeoutExpired) as exc:
            _log(f"amend: refused ({type(exc).__name__})")
            self._reply(503, {"error": "amend-failed", "detail": type(exc).__name__})
            return
        _log(f"amend: {uuid}")
        self._reply(200, {"status": "amended", "uuid": uuid, "response": written})

    def log_message(self, fmt, *args):
        # Keep bridge logging minimal: outcomes only. Never echo a request or a
        # token (NFR-04).
        return


def main():
    _log(f"start host={HOST} port={PORT} phase=p4b-write authMode=per-request")
    ThreadingHTTPServer((HOST, PORT), Handler).serve_forever()


if __name__ == "__main__":
    main()
