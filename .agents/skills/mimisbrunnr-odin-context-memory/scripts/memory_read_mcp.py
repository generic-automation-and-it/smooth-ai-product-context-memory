#!/usr/bin/env python3
"""Dependency-free stdio MCP server exposing context-memory reads only."""

import json
import os
import sys

import context_memory_client as client
import deepsearch

SERVER_NAME = "mimisbrunnr-read"

TOOLS = [
    ("probe", "Check API reachability", {}),
    ("query", "Query bounded cheap memory fields", {"payload": {"type": "object"}}),
    ("deepsearch", "Run bounded opt-in recall expansion", {"payload": {"type": "object"}}),
    ("get_versions", "Read version history", {"uuid": {"type": "string"}, "scope": {"type": ["string", "null"]}}),
    ("get_blob", "Read one selected blob", {"uuid": {"type": "string"}, "version": {"type": "integer"}, "scope": {"type": ["string", "null"]}}),
    ("paths", "Read bounded memory paths", {"payload": {"type": "object"}}),
    ("ticket_paths", "Read bounded declared ticket paths", {"payload": {"type": "object"}}),
    ("labels", "List label registry and usage", {}),
    ("initiatives", "List initiatives", {"status": {"type": ["string", "null"]}}),
]


# Framing is the default; only a tool that returns no recalled content opts out. `probe` reports whether
# a TCP connection succeeds and returns no record, statement or body. Derived from TOOLS, so a tool added
# without a framing decision is a test failure rather than a silently unframed surface — the same shape as
# the read CLI's `READ_COMMANDS`/`UNFRAMED_COMMANDS` split. The framing itself is the one shared notice in
# `context_memory_client.framed_recall`, so the MCP server and the CLI cannot drift into two wordings.
UNFRAMED_TOOLS = frozenset({"probe"})
TOOL_NAMES = frozenset(name for name, _description, _schema in TOOLS)
FRAMED_TOOLS = frozenset(TOOL_NAMES - UNFRAMED_TOOLS)


def tool_definitions():
    definitions = []
    for name, description, properties in TOOLS:
        required = [key for key, value in properties.items() if "null" not in value.get("type", [])]
        definitions.append({
            "name": name,
            "description": description,
            "inputSchema": {
                "type": "object",
                "properties": properties,
                "required": required,
                "additionalProperties": False,
            },
        })
    return definitions


def call_tool(name, arguments):
    if name == "probe":
        return {"reachable": client._probe(client.base_url())}
    if name == "query":
        payload = dict(arguments["payload"])
        payload.setdefault("limit", client.MAX_QUERY_LIMIT)
        return client._request("POST", "/api/context/query", payload)
    if name == "deepsearch":
        return deepsearch.execute(arguments["payload"])
    if name == "get_versions":
        query = {"scope": arguments["scope"]} if arguments.get("scope") else None
        return client._request("GET", client.memory_versions_path(arguments.get("uuid")), query=query)
    if name == "get_blob":
        return _get_blob(arguments)
    if name == "paths":
        return client._request("POST", "/api/context/paths", arguments["payload"])
    if name == "ticket_paths":
        payload = dict(arguments["payload"])
        payload.setdefault("direction", "outbound")
        payload.setdefault("pathLimit", 50)
        payload.setdefault("memoryLimit", 50)
        return client._request("POST", "/api/context/tickets/paths", payload)
    if name == "labels":
        return client._request("GET", "/api/context/labels")
    if name == "initiatives":
        query = {"status": arguments["status"]} if arguments.get("status") else None
        return client._request("GET", "/api/context/initiatives", query=query)
    raise ValueError(f"Unknown read tool '{name}'")


def _get_blob(arguments):
    from urllib.parse import urlencode

    url = client.base_url() + client.memory_blob_path(arguments.get("uuid"), arguments.get("version"))
    if arguments.get("scope"):
        url += "?" + urlencode({"scope": arguments["scope"]})
    token = os.environ.get(client.ENV_READ_TOKEN)
    if not token:
        raise client.ClientError(0, "missing-credential", f"{client.ENV_READ_TOKEN} is required")
    request = client.urllib.request.Request(url, headers={"Authorization": f"Bearer {token}"}, method="GET")
    # The shared classified path, not `_open` directly: it applies the recall deadline and turns a hang
    # into a `timed-out` ClientError, so the blob fetch is bounded and classified like every other read
    # instead of surfacing a bare `timed out` after the socket timeout.
    return {"content": client._read_response(request)}


def handle(message):
    method = message.get("method")
    request_id = message.get("id")
    if method == "initialize":
        result = {
            "protocolVersion": "2025-06-18",
            "capabilities": {"tools": {}},
            "serverInfo": {"name": SERVER_NAME, "version": "1.0.0"},
        }
    elif method == "tools/list":
        result = {"tools": tool_definitions()}
    elif method == "ping":
        result = {}
    elif method == "tools/call":
        params = message.get("params", {})
        name = params.get("name")
        value = call_tool(name, params.get("arguments", {}))
        # Every content-returning tool is framed as untrusted data, from the one shared notice, because
        # the read worker's only path to the store is this server — the CLI it cannot reach is not the
        # surface that matters. `probe` returns no record and opts out.
        if name in FRAMED_TOOLS:
            value = client.framed_recall(value)
        result = {"content": [{"type": "text", "text": json.dumps(value)}]}
    elif request_id is None:
        return None
    else:
        return {"jsonrpc": "2.0", "id": request_id, "error": {"code": -32601, "message": "Method not found"}}
    return {"jsonrpc": "2.0", "id": request_id, "result": result}


def respond(line, handler):
    try:
        message = json.loads(line)
    except ValueError as error:
        return {"jsonrpc": "2.0", "id": None,
                "error": {"code": -32700, "message": f"Parse error: {error}"}}
    if not isinstance(message, dict):
        return {"jsonrpc": "2.0", "id": None,
                "error": {"code": -32600, "message": "Invalid Request: message must be an object"}}
    try:
        return handler(message)
    except Exception as error:
        return {"jsonrpc": "2.0", "id": message.get("id"),
                "error": {"code": -32000, "message": str(error)}}


def main():
    os.environ.pop(client.ENV_WRITE_TOKEN, None)
    for line in sys.stdin:
        if not line.strip():
            continue
        response = respond(line, handle)
        if response is not None:
            print(json.dumps(response, separators=(",", ":")), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
