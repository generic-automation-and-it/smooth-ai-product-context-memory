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
        return client._request("GET", f"/api/context/memories/{arguments['uuid']}/versions", query=query)
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

    url = client.base_url() + f"/api/context/memories/{arguments['uuid']}/versions/{arguments['version']}/blob"
    if arguments.get("scope"):
        url += "?" + urlencode({"scope": arguments["scope"]})
    token = os.environ.get(client.ENV_READ_TOKEN)
    if not token:
        raise client.ClientError(0, "missing-credential", f"{client.ENV_READ_TOKEN} is required")
    request = client.urllib.request.Request(url, headers={"Authorization": f"Bearer {token}"}, method="GET")
    with client._open(request) as response:
        return {"content": response.read().decode("utf-8")}


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
        value = call_tool(params.get("name"), params.get("arguments", {}))
        result = {"content": [{"type": "text", "text": json.dumps(value)}]}
    elif request_id is None:
        return None
    else:
        return {"jsonrpc": "2.0", "id": request_id, "error": {"code": -32601, "message": "Method not found"}}
    return {"jsonrpc": "2.0", "id": request_id, "result": result}


def main():
    os.environ.pop(client.ENV_WRITE_TOKEN, None)
    for line in sys.stdin:
        if not line.strip():
            continue
        try:
            response = handle(json.loads(line))
        except Exception as error:
            response = {
                "jsonrpc": "2.0",
                "id": json.loads(line).get("id"),
                "error": {"code": -32000, "message": str(error)},
            }
        if response is not None:
            print(json.dumps(response, separators=(",", ":")), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
