#!/usr/bin/env python3
"""Deterministic plumbing for the context-memory skill.

Thin, dependency-free client over the WT-2 HTTP API. Every byte of judgement lives in SKILL.md; this
script only moves JSON. Reads the request body from a payload file or stdin, performs the HTTP call,
and prints the response JSON on stdout. Nothing is ever logged that leaks memory content.
"""

import argparse
import json
import os
import sys
import urllib.error
import urllib.request

DEFAULT_BASE_URL = "http://localhost:5141"
ENV_BASE_URL = "CONTEXT_MEMORY_BASE_URL"

# Static, configurable candidate cap (decision 6). Change this constant to widen or narrow the
# preflight batch without touching pipeline logic. Mirrors Preflight.MaxCandidates.
MAX_CANDIDATES = 20

# Query recall limit for the semantic-dedup surface (decision 1). Mirrors MemorySearchDefaults.MaxLimit.
MAX_QUERY_LIMIT = 200

HTTP_TIMEOUT = 30


class ClientError(RuntimeError):
    """A non-success HTTP response, surfaced as a machine-readable error."""

    def __init__(self, status, status_text, body):
        super().__init__(f"HTTP {status} {status_text}: {body}")
        self.status = status
        self.status_text = status_text
        self.body = body


def base_url():
    return os.environ.get(ENV_BASE_URL, DEFAULT_BASE_URL).rstrip("/")


def _request(method, path, payload=None, query=None):
    url = base_url() + path
    if query:
        from urllib.parse import urlencode

        url += "?" + urlencode(query)

    body = None
    headers = {"Accept": "application/json"}
    if payload is not None:
        body = json.dumps(payload).encode("utf-8")
        headers["Content-Type"] = "application/json"

    req = urllib.request.Request(url, data=body, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=HTTP_TIMEOUT) as resp:
            raw = resp.read().decode("utf-8")
            return json.loads(raw) if raw else None
    except urllib.error.HTTPError as e:
        err_body = e.read().decode("utf-8", errors="replace")
        raise ClientError(e.code, e.reason, err_body) from e
    except urllib.error.URLError as e:
        raise ClientError(0, "unreachable", str(e.reason)) from e


def _probe(base):
    """True if the API host accepts a TCP connection. Honest, never a silent miss."""
    from urllib.parse import urlparse

    parsed = urlparse(base)
    host = parsed.hostname or "localhost"
    port = parsed.port or (443 if parsed.scheme == "https" else 80)
    try:
        import socket

        with socket.create_connection((host, port), timeout=HTTP_TIMEOUT):
            return True
    except OSError:
        return False


def cmd_probe(args):
    base = args.base_url or base_url()
    if _probe(base):
        print(f"context-memory API reachable at {base}")
        return
    print(f"context-memory API unreachable at {base}", file=sys.stderr)
    sys.exit(2)


def read_payload(path):
    if path:
        with open(path, "r", encoding="utf-8") as fh:
            return json.load(fh)
    return json.load(sys.stdin)


def cmd_preflight(args):
    """POST /api/context/preflight. Array-in/array-out, refuses over-cap batches.

    The API numbers candidate indices (and intra-batch collision left/right indices)
    from position within a single request. Chunking an over-cap batch and merging
    responses would report chunk-local indices as batch indices and lose cross-chunk
    collisions, so over-cap batches are refused outright — same contract as cmd_set.
    """
    payload = read_payload(args.payload)
    candidates = payload.get("candidates", payload)
    if not isinstance(candidates, list):
        raise ClientError(0, "bad-input", "'candidates' must be a list")
    if len(candidates) > MAX_CANDIDATES:
        raise ClientError(
            0,
            "bad-input",
            f"Batch has {len(candidates)} candidates; cap is {MAX_CANDIDATES}. "
            "Split into multiple checkpoints.",
        )

    resp = _request("POST", "/api/context/preflight", {"candidates": candidates})
    out = {
        "candidates": resp.get("candidates", []),
        "intra_batch_collisions": resp.get("intraBatchCollisions", []),
    }
    print(json.dumps(out, indent=2))
    return out


def cmd_set(args):
    """POST /api/context/memories. --dryrun appends ?dryRun=true."""
    payload = read_payload(args.payload)
    if not isinstance(payload, dict):
        raise ClientError(0, "bad-input", "'set' payload must be an object with 'items'")
    if len(payload.get("items", [])) > MAX_CANDIDATES:
        raise ClientError(
            0,
            "bad-input",
            f"Batch has {len(payload['items'])} items; cap is {MAX_CANDIDATES}. "
            "Split into multiple checkpoints.",
        )
    query = {"dryRun": "true"} if args.dryrun else None
    resp = _request("POST", "/api/context/memories", payload, query=query)
    print(json.dumps(resp, indent=2))
    return resp


def cmd_query(args):
    """POST /api/context/query. Semantic-dedup recall surface."""
    payload = read_payload(args.payload)
    if not isinstance(payload, dict):
        raise ClientError(0, "bad-input", "'query' payload must be an object")
    if "limit" not in payload:
        payload["limit"] = MAX_QUERY_LIMIT
    resp = _request("POST", "/api/context/query", payload)
    print(json.dumps(resp, indent=2))
    return resp


def cmd_get_versions(args):
    resp = _request(
        "GET",
        f"/api/context/memories/{args.uuid}/versions",
        query={"scope": args.scope} if args.scope else None,
    )
    print(json.dumps(resp, indent=2))
    return resp


def cmd_get_blob(args):
    """Returns the blob body as raw text (it is not JSON), so scope enforcement stays the API's job."""
    from urllib.parse import urlencode

    url = base_url() + f"/api/context/memories/{args.uuid}/versions/{args.version}/blob"
    if args.scope:
        url += "?" + urlencode({"scope": args.scope})
    req = urllib.request.Request(url, method="GET")
    try:
        with urllib.request.urlopen(req, timeout=HTTP_TIMEOUT) as resp:
            print(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        raise ClientError(e.code, e.reason, e.read().decode("utf-8", errors="replace")) from e
    except urllib.error.URLError as e:
        raise ClientError(0, "unreachable", str(e.reason)) from e


def cmd_resolve_group(args):
    resp = _request("POST", "/api/context/groups/resolve", read_payload(args.payload))
    print(json.dumps(resp, indent=2))
    return resp


def cmd_update_group(args):
    resp = _request("PATCH", f"/api/context/groups/{args.uuid}", read_payload(args.payload))
    print(json.dumps(resp, indent=2))
    return resp


def cmd_append_description(args):
    resp = _request(
        "POST", f"/api/context/groups/{args.uuid}/descriptions", read_payload(args.payload)
    )
    print(json.dumps(resp, indent=2))
    return resp


def cmd_create_link(args):
    resp = _request("POST", "/api/context/links", read_payload(args.payload))
    print(json.dumps(resp, indent=2))
    return resp


def cmd_labels(args):
    resp = _request("GET", "/api/context/labels")
    print(json.dumps(resp, indent=2))
    return resp


def cmd_propose_label(args):
    resp = _request("POST", "/api/context/labels", read_payload(args.payload))
    print(json.dumps(resp, indent=2))
    return resp


def cmd_initiatives(args):
    resp = _request("GET", "/api/context/initiatives", query={"status": args.status} if args.status else None)
    print(json.dumps(resp, indent=2))
    return resp


def cmd_upsert_initiative(args):
    resp = _request("POST", "/api/context/initiatives", read_payload(args.payload))
    print(json.dumps(resp, indent=2))
    return resp


def main():
    parser = argparse.ArgumentParser(prog="context_memory_client")
    parser.add_argument("--base-url", help="override " + ENV_BASE_URL)
    sub = parser.add_subparsers(dest="command", required=True)

    p = sub.add_parser("probe", help="honest availability check; exit 2 if unreachable")
    p.set_defaults(func=cmd_probe)

    p = sub.add_parser("preflight", help="POST /api/context/preflight (batch, array-in/out)")
    p.add_argument("--payload", help="JSON file; defaults to stdin")
    p.set_defaults(func=cmd_preflight)

    p = sub.add_parser("set", help="POST /api/context/memories")
    p.add_argument("--payload", help="JSON file; defaults to stdin")
    p.add_argument("--dryrun", action="store_true", help="append ?dryRun=true; write nothing")
    p.set_defaults(func=cmd_set)

    p = sub.add_parser("query", help="POST /api/context/query (semantic-dedup recall)")
    p.add_argument("--payload", help="JSON file; defaults to stdin")
    p.set_defaults(func=cmd_query)

    p = sub.add_parser("get-versions", help="GET /api/context/memories/{uuid}/versions")
    p.add_argument("uuid")
    p.add_argument("--scope", help="scope dimension the caller reads as (scope boundary)")
    p.set_defaults(func=cmd_get_versions)

    p = sub.add_parser("get-blob", help="GET /api/context/memories/{uuid}/versions/{v}/blob")
    p.add_argument("uuid")
    p.add_argument("version", type=int)
    p.add_argument("--scope", help="scope dimension the caller reads as (scope boundary)")
    p.set_defaults(func=cmd_get_blob)

    p = sub.add_parser("resolve-group", help="POST /api/context/groups/resolve")
    p.add_argument("--payload", help="JSON file; defaults to stdin")
    p.set_defaults(func=cmd_resolve_group)

    p = sub.add_parser("update-group", help="PATCH /api/context/groups/{uuid}")
    p.add_argument("uuid")
    p.add_argument("--payload", help="JSON file; defaults to stdin")
    p.set_defaults(func=cmd_update_group)

    p = sub.add_parser("append-description", help="POST /api/context/groups/{uuid}/descriptions")
    p.add_argument("uuid")
    p.add_argument("--payload", help="JSON file; defaults to stdin")
    p.set_defaults(func=cmd_append_description)

    p = sub.add_parser("create-link", help="POST /api/context/links")
    p.add_argument("--payload", help="JSON file; defaults to stdin")
    p.set_defaults(func=cmd_create_link)

    p = sub.add_parser("labels", help="GET /api/context/labels")
    p.set_defaults(func=cmd_labels)

    p = sub.add_parser("propose-label", help="POST /api/context/labels")
    p.add_argument("--payload", help="JSON file; defaults to stdin")
    p.set_defaults(func=cmd_propose_label)

    p = sub.add_parser("initiatives", help="GET /api/context/initiatives")
    p.add_argument("--status", help="active or archived")
    p.set_defaults(func=cmd_initiatives)

    p = sub.add_parser("upsert-initiative", help="POST /api/context/initiatives")
    p.add_argument("--payload", help="JSON file; defaults to stdin")
    p.set_defaults(func=cmd_upsert_initiative)

    args = parser.parse_args()

    if args.base_url:
        os.environ[ENV_BASE_URL] = args.base_url

    try:
        args.func(args)
    except ClientError as e:
        print(str(e), file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
