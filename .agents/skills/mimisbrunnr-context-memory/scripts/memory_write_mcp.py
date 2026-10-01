#!/usr/bin/env python3
"""Dependency-free stdio MCP server exposing typed memory workflow tools."""

import json
import sys

import atomicity
import authority
import context_memory_client as client
import deepsearch
import divergence
import memory_read_mcp
import redact

SERVER_NAME = "mimisbrunnr-write"
READ_TOOLS = {tool[0] for tool in memory_read_mcp.TOOLS}
WRITE_TOOLS = ("preflight", "set", "resolve_group", "update_group", "append_description",
               "create_link", "ticket_parent", "propose_label", "upsert_initiative",
               "redact", "atomicity", "authority", "divergence")


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
        # Redaction is not a separate tool here. A tool the caller must remember is a tool that
        # gets skipped, and the cost of skipping it is a permanent blob: content addressing means
        # a leaked secret can be orphaned but never edited out. Fail closed like the CLI path.
        payload, hits = client.scrub_or_refuse(payload)
        query = {"dryRun": "true"} if arguments.get("dryRun") else None
        result = client._request("POST", "/api/context/memories", payload, query=query)
        return client.attach_redaction(result, hits)
    if name == "update_group":
        return client._request("PATCH", client.group_path(arguments.get("uuid")), payload)
    if name == "append_description":
        return client._request("POST", client.group_descriptions_path(arguments.get("uuid")), payload)
    if name == "redact":
        results = []
        for index, content in enumerate(arguments["items"]):
            redacted, located = redact.scrub_located(content)
            results.append({"index": index, "redacted": redacted,
                            "findings": redact.findings_for(located)})
        return {"results": results}
    if name == "atomicity":
        return {"results": [atomicity.classify(item.get("statement") or item.get("description", ""))
                            for item in arguments["items"]]}
    if name == "divergence":
        return divergence.compose(payload)
    if name == "authority":
        return authority.compose(payload)
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
        response = memory_read_mcp.respond(line, handle)
        if response is not None:
            print(json.dumps(response, separators=(",", ":")), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
