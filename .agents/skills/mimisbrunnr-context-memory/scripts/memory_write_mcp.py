#!/usr/bin/env python3
"""Dependency-free stdio MCP server exposing typed memory workflow tools."""

import json
import sys

import atomicity
import context_memory_client as client
import deepsearch
import divergence
import memory_read_mcp
import redact

SERVER_NAME = "mimisbrunnr-write"
READ_TOOLS = {tool[0] for tool in memory_read_mcp.TOOLS}
WRITE_TOOLS = ("preflight", "set", "resolve_group", "update_group", "append_description",
               "create_link", "ticket_parent", "propose_label", "upsert_initiative",
               "redact", "atomicity", "divergence")


def _schema(properties, required=None):
    return {"type": "object", "properties": properties, "required": required or [],
            "additionalProperties": False}


def tool_definitions():
    definitions = memory_read_mcp.tool_definitions()
    payload = {"payload": {"type": "object"}}
    for name in WRITE_TOOLS:
        properties = dict(payload)
        required = ["payload"]
        if name in ("update_group", "append_description"):
            properties["uuid"] = {"type": "string"}
            required.append("uuid")
        if name in ("redact", "atomicity"):
            properties = {"items": {"type": "array"}}
            required = ["items"]
        if name == "set":
            properties["dryRun"] = {"type": "boolean"}
        definitions.append({"name": name, "description": f"Memory workflow: {name}",
                            "inputSchema": _schema(properties, required)})
    return definitions


def call_tool(name, arguments):
    if name in READ_TOOLS:
        return memory_read_mcp.call_tool(name, arguments)
    payload = arguments.get("payload", {})
    calls = {
        "preflight": ("POST", "/api/context/preflight"),
        "resolve_group": ("POST", "/api/context/groups/resolve"),
        "create_link": ("POST", "/api/context/links"),
        "ticket_parent": ("PUT", "/api/context/tickets/parent"),
        "propose_label": ("POST", "/api/context/labels"),
        "upsert_initiative": ("POST", "/api/context/initiatives"),
    }
    if name in calls:
        method, path = calls[name]
        return client._request(method, path, payload)
    if name == "set":
        client.validate_set_payload(payload)
        query = {"dryRun": "true"} if arguments.get("dryRun") else None
        return client._request("POST", "/api/context/memories", payload, query=query)
    if name == "update_group":
        return client._request("PATCH", f"/api/context/groups/{arguments['uuid']}", payload)
    if name == "append_description":
        return client._request("POST", f"/api/context/groups/{arguments['uuid']}/descriptions", payload)
    if name == "redact":
        results = []
        for index, content in enumerate(arguments["items"]):
            redacted, findings = redact._scrub(content)
            results.append({"index": index, "redacted": redacted, "findings": [
                {"rule_name": rule, "hit_count": count} for rule, count in sorted(findings.items())]})
        return {"results": results}
    if name == "atomicity":
        return {"results": [atomicity.classify(item.get("statement") or item.get("description", ""))
                            for item in arguments["items"]]}
    if name == "divergence":
        return divergence.compose(payload)
    raise ValueError(f"Unknown write tool '{name}'")


def handle(message):
    method = message.get("method")
    request_id = message.get("id")
    if method == "initialize":
        result = {"protocolVersion": "2025-06-18", "capabilities": {"tools": {}},
                  "serverInfo": {"name": SERVER_NAME, "version": "1.0.0"}}
    elif method == "ping":
        result = {}
    elif method == "tools/list":
        result = {"tools": tool_definitions()}
    elif method == "tools/call":
        params = message.get("params", {})
        value = call_tool(params.get("name"), params.get("arguments", {}))
        result = {"content": [{"type": "text", "text": json.dumps(value)}]}
    elif request_id is None:
        return None
    else:
        return {"jsonrpc": "2.0", "id": request_id,
                "error": {"code": -32601, "message": "Method not found"}}
    return {"jsonrpc": "2.0", "id": request_id, "result": result}


def main():
    for line in sys.stdin:
        if not line.strip():
            continue
        message = json.loads(line)
        try:
            response = handle(message)
        except Exception as error:
            response = {"jsonrpc": "2.0", "id": message.get("id"),
                        "error": {"code": -32000, "message": str(error)}}
        if response is not None:
            print(json.dumps(response, separators=(",", ":")), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
