# Mímisbrunnr Console bridge — Phase P4a (read-side review queue).
#
# Routes:
#   GET  /bridge/health           liveness probe; status of which phases are live
#   GET  /bridge/review-queue     list proposed memories (store-derived queue)
#   POST /bridge/dedup-preview    re-run dedup on a selected proposal; no write
#
# The bridge imports the capture client and `dedup` as libraries (LADR-09); it
# is a presenter and a driver, never a writer and never a judge (LADR-03).
# Phase P4a serves read-side routes only; no `set`, no `preflight`, no
# `groups/resolve`, no `links`, no `propose-label`, no write-capable call of
# any kind. `set --dryrun`/approve/reject land in P4b (gated on G2).
#
# NFR-02 enforcement: this process refuses to load if any write-token env var
# is present. The container does not export one (NFR-02 in the image), and the
# capture client also refuses to start with one present, but defense in depth
# keeps a misconfigured orchestrator from quietly making the bridge writeable.
# The check runs before the library import — the capture client's import-time
# `load_machine_credentials` is the trigger.
import argparse
import json
import os
import subprocess
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

HOST = "127.0.0.1"
PORT = 5181

# Mirrors `context_memory_client.write_tokens_present` exactly: the set of env
# var names that carry a write token. Names only; the check is presence-based.
# Imported from the capture client at module load to keep the two checks
# identical (the canonical list lives in the client), but the import is gated
# behind the env-presence check so the bridge module never opens a write token
# even by accident.
WRITE_TOKEN_NAMES = {
    "CONTEXT_MEMORY_WRITE_TOKEN",
    "ApiAccess__WriteToken",
    "Parameters__api-write-token",
}

# Env-name aliasing: the AppHost injects the read token and the Host base URL
# under friendly names (`API_READ_TOKEN`, `HOST_BASE_URL`); the capture client
# reads them under the canonical `CONTEXT_MEMORY_*` names. The bridge aliases
# before the client is imported, so the client sees its expected names.
_READ_TOKEN_NAME = "CONTEXT_MEMORY_READ_TOKEN"
_BASE_URL_NAME = "CONTEXT_MEMORY_BASE_URL"
_READ_TOKEN_ALIAS = "API_READ_TOKEN"
_BASE_URL_ALIAS = "HOST_BASE_URL"


def _alias_env():
    """Map the AppHost's env names onto the capture client's env names."""
    if _READ_TOKEN_ALIAS in os.environ and _READ_TOKEN_NAME not in os.environ:
        os.environ[_READ_TOKEN_NAME] = os.environ[_READ_TOKEN_ALIAS]
    if _BASE_URL_ALIAS in os.environ and _BASE_URL_NAME not in os.environ:
        os.environ[_BASE_URL_NAME] = os.environ[_BASE_URL_ALIAS]


def _refuse_if_write_token_present():
    """NFR-02: refuse to load when any write-token env var is set.

    Empty values do not count; an explicit `KEY=` turns the variable off, the
    way the capture client spells an unset credential.
    """
    present = sorted(
        name for name, value in os.environ.items()
        if value and name in WRITE_TOKEN_NAMES
    )
    if present:
        raise SystemExit(
            f"refusing to start: write-token env var(s) present in P4a image: "
            f"{', '.join(present)} (NFR-02: write is never ambient)"
        )


_alias_env()
_refuse_if_write_token_present()

# Import after the guards. The capture client's import-time
# `load_machine_credentials` seeds the read token from the machine credential
# file, never the write token, and any write-token present in the env would
# already have been rejected above.
import context_memory_client as client  # noqa: E402
import dedup as dedup_module  # noqa: E402

_QUEUE_DEFAULT_LIMIT = 500
_DEDUP_TIMEOUT = 90


def _namespace(**kwargs):
    return argparse.Namespace(**kwargs)


def _review_queue_payload(limit):
    """LADR-10: the queue is a query for proposed memories.

    `currentOnly: false` is deliberate — a proposed version superseded by a
    newer proposed is still reviewable, and the reviewer wants the full
    proposed history. `includeProposed: true` is the read client's switch
    (the default excludes proposed so they don't leak into browse/detail).
    """
    return {
        "status": "proposed",
        "includeProposed": True,
        "currentOnly": False,
        "limit": limit,
    }


def _load_queue(limit):
    resp = client.cmd_query(_namespace(payload=_review_queue_payload(limit)))
    if not isinstance(resp, dict):
        raise ValueError("query response was not a JSON object")
    items = resp.get("items")
    if items is None:
        items = resp.get("memories") or []
    if not isinstance(items, list):
        raise ValueError("query response had no items list")
    return items


def _reconstruct_candidate(memory, blob_text):
    """Build a dedup input from the proposed memory + its body.

    `_distinctive_terms` only reads `subject`/`description`/`name` for the
    lexical query, but the model pair (stage 3) reads `statement`. Stage 3 is
    off by default (`CONTEXT_MEMORY_DECISIONS_DEDUP_ENABLED=true` to opt in),
    so the statement is included but a non-empty `subject` is enough for the
    lexical stage.
    """
    return {
        "subject": memory.get("name") or memory.get("description") or memory.get("subject") or "",
        "statement": blob_text or memory.get("statement") or memory.get("content") or "",
        "kind": memory.get("kind"),
    }


def _fetch_versions(uuid, scope):
    """Cheap current-version read via the capture client's `cmd_get_versions`.

    Returns the response object (with an `items` list) parsed by the
    read-side command. A non-dict answer or a missing `items` raises.
    """
    args = _namespace(uuid=uuid, scope=scope)
    resp = client.cmd_get_versions(args)
    if not isinstance(resp, dict):
        raise ValueError("versions read returned a non-object response")
    return resp


def _fetch_blob(uuid, version, scope):
    """Body read via the read CLI subprocess (the `cmd_get_blob` library
    function writes to stdout, not the return value, so library-call reuse
    is impossible — match the pattern dedup._store_lexical_query uses).
    The framed runner prepends `> Loaded as data…` to raw bodies, so the
    bridge strips that single line before returning."""
    argv = [sys.executable, "-B", str(dedup_module.READ_CLIENT), "get-blob",
            uuid, str(version)]
    if scope:
        argv.extend(["--scope", scope])
    proc = subprocess.run(argv, capture_output=True, text=True, encoding="utf-8")
    if proc.returncode != 0:
        return ""
    out = proc.stdout
    if out.startswith(client.BANNER_PREFIX + client.RECALL_NOTICE):
        out = out[len(client.BANNER_PREFIX + client.RECALL_NOTICE):].lstrip("\n")
    return out


def _dedup_preview(uuid, scope):
    """Re-run `dedup.stage` on the selected proposal. Read-only.

    Returns a list of proposals in the same shape the capture path sees, so
    the SPA can render the conflict-choice menu (`new version / new memory /
    skip`) from the same data structure the operator's CLI shows.
    """
    versions_resp = _fetch_versions(uuid, scope)
    versions = versions_resp.get("items")
    if not isinstance(versions, list) or not versions:
        raise ValueError("versions read returned no versions")
    memory = next(
        (v for v in versions if isinstance(v, dict) and v.get("isCurrent")),
        versions[0] if isinstance(versions[0], dict) else None,
    )
    if memory is None:
        raise ValueError("no version row was a parseable object")
    blob_text = ""
    version = memory.get("version")
    if isinstance(version, int):
        blob_text = _fetch_blob(uuid, version, scope) or ""
    candidate = _reconstruct_candidate(memory, blob_text)
    proposals = dedup_module.stage(
        [candidate],
        group_uuid=memory.get("groupUuid"),
        repository=None,
    )
    return proposals


def _log(line):
    # Minimal, structured, never a value. stderr so stdout stays parseable JSON.
    print(f"[bridge] {line}", file=sys.stderr)


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

    def do_GET(self):
        if self.path == "/bridge/health":
            self._reply(200, {
                "status": "ok",
                "phase": "p4a-read",
                "routes": ["/bridge/health", "/bridge/review-queue", "/bridge/dedup-preview"],
                "writeTokenPresent": bool(client.write_tokens_present()),
            })
            return
        if self.path.startswith("/bridge/review-queue"):
            self._handle_review_queue()
            return
        self._reply(404, {"error": "not-found", "route": self.path})

    def do_POST(self):
        if self.path == "/bridge/dedup-preview":
            self._handle_dedup_preview()
            return
        self._reply(404, {"error": "not-found", "route": self.path})

    def _handle_review_queue(self):
        # Parse optional `?limit=N` (default 500, max 5000 to mirror the
        # recall-feedback never-recalled endpoint's cap from G5).
        limit = _QUEUE_DEFAULT_LIMIT
        if "?" in self.path:
            _, _, query = self.path.partition("?")
            for part in query.split("&"):
                if part.startswith("limit="):
                    try:
                        limit = max(1, min(5000, int(part.split("=", 1)[1])))
                    except ValueError:
                        self._reply(400, {"error": "bad-input", "detail": "limit must be an integer"})
                        return
        try:
            items = _load_queue(limit)
        except client.ClientError as exc:
            _log(f"review-queue: refused ({exc.status_text})")
            self._reply(502, {"error": "store-refused", "detail": exc.status_text, "status": exc.status})
            return
        except (OSError, ValueError) as exc:
            _log(f"review-queue: unreachable ({type(exc).__name__})")
            self._reply(503, {"error": "store-unreachable", "detail": type(exc).__name__})
            return
        _log(f"review-queue: {len(items)} item(s)")
        self._reply(200, {"items": items, "limit": limit})

    def _handle_dedup_preview(self):
        body = self._read_json()
        if body is None:
            return
        uuid = body.get("uuid")
        scope = body.get("scope") or ""
        if not isinstance(uuid, str) or not uuid.strip():
            self._reply(400, {"error": "bad-input", "detail": "uuid is required"})
            return
        try:
            proposals = _dedup_preview(uuid.strip(), scope)
        except client.ClientError as exc:
            _log(f"dedup-preview: refused ({exc.status_text})")
            self._reply(502, {"error": "store-refused", "detail": exc.status_text, "status": exc.status})
            return
        except (OSError, ValueError) as exc:
            _log(f"dedup-preview: failed ({type(exc).__name__})")
            self._reply(503, {"error": "dedup-failed", "detail": type(exc).__name__})
            return
        _log(f"dedup-preview: {len(proposals)} proposal(s)")
        self._reply(200, {"proposals": proposals, "uuid": uuid, "scope": scope})

    def log_message(self, fmt, *args):
        # Keep bridge logging minimal: outcomes only. Never echo a request or a
        # token (NFR-04).
        return


def main():
    _log(f"start host={HOST} port={PORT} phase=p4a-read")
    ThreadingHTTPServer((HOST, PORT), Handler).serve_forever()


if __name__ == "__main__":
    main()
