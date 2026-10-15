#!/usr/bin/env python3
"""Bounded, offline evidence validation and reporting. Semantic judgement stays with the skill."""

import json
import sys
from uuid import UUID

MAX_BYTES = 1024 * 1024
MAX_RECORDS = 200
MAX_TAGS = 200


def _object(value, fields, name):
    if not isinstance(value, dict) or set(value) != set(fields.split()):
        raise ValueError(f"{name}: incorrect object fields")


def _text(value, name, limit=8000):
    if not isinstance(value, str) or not value.strip() or len(value) > limit:
        raise ValueError(f"{name}: requires bounded non-empty text")


def _list(value, name, limit=MAX_RECORDS):
    if not isinstance(value, list) or len(value) > limit:
        raise ValueError(f"{name}: requires an array of at most {limit} items")


def _reference(value):
    uuid = value.get("uuid")
    try:
        if not isinstance(uuid, str) or str(UUID(uuid)) != uuid or UUID(uuid).int == 0:
            raise ValueError()
    except ValueError:
        raise ValueError("reference: requires a canonical non-zero UUID") from None
    version = value.get("version")
    if type(version) is not int or version < 1:
        raise ValueError("reference: requires a positive integer version")
    return uuid, version


def _tags(value):
    _list(value, "tags", MAX_TAGS)
    for tag in value:
        _text(tag, "tag", 256)
    if len(set(value)) != len(value):
        raise ValueError("tags: duplicates are not permitted")


def _record_scope(value):
    dimension, identifier = value["scopeDimension"], value["scopeIdentifier"]
    if dimension not in ("product", "customer", "program", "self"):
        raise ValueError("scopeDimension: requires product, customer, program or self")
    if identifier is not None:
        _text(identifier, "scopeIdentifier", 200)
    if dimension in ("customer", "program") and identifier is None:
        raise ValueError("scopeIdentifier: required for customer/program evidence")
    return dimension, identifier


def build_report(payload):
    """Validate declared authorization and supplied analysis without discovering any evidence."""
    if len(json.dumps(payload, allow_nan=False).encode("utf-8")) > MAX_BYTES:
        raise ValueError("input exceeds the byte cap; narrow the approved evidence explicitly")
    _object(payload, "scope originalQuery records analyses selected disclosure", "input")
    scope = payload["scope"]
    _object(scope, "name authorization records", "scope")
    _text(scope["name"], "scope.name")
    if scope["authorization"] not in ("explicitly-supplied", "already-authorized"):
        raise ValueError("scope.authorization: explicit supplied/authorized evidence required")
    _list(scope["records"], "scope.records")
    approved = {}
    for ref in scope["records"]:
        _object(ref, "uuid version scopeDimension scopeIdentifier", "scope reference")
        key = _reference(ref)
        if key in approved:
            raise ValueError("scope.records: duplicate reference")
        approved[key] = _record_scope(ref)

    query = payload["originalQuery"]
    query_fields = set("query facets tags kind status scopeDimension groupUuid ticketProvider ticketKey "
                       "repo initiativeName includeProposed currentOnly asOf limit facetMatchMode".split())
    if not isinstance(query, dict) or "tags" not in query or query.keys() - query_fields:
        raise ValueError("originalQuery: requires the original API query with tags and supported fields")
    _tags(query["tags"])
    match_mode = query.get("facetMatchMode", "any")
    if not query["tags"] or match_mode not in ("any", "all"):
        raise ValueError("originalQuery: non-empty exact tags and any/all facetMatchMode required")
    if not isinstance(payload["disclosure"], dict):
        raise ValueError("disclosure must be an object")

    _list(payload["records"], "records")
    records = {}
    for record in payload["records"]:
        _object(record, "uuid version tags status scopeDimension scopeIdentifier statement", "record")
        key = _reference(record)
        if key not in approved or _record_scope(record) != approved[key]:
            raise ValueError("record outside the declared approved scope")
        if key in records:
            raise ValueError("records: duplicate reference")
        _tags(record["tags"])
        if record["status"] not in ("approved", "proposed"):
            raise ValueError("record.status: requires approved or proposed")
        _text(record["statement"], "record.statement")
        records[key] = record

    _list(payload["selected"], "selected")
    selected = set()
    for ref in payload["selected"]:
        _object(ref, "uuid version", "selected reference")
        key = _reference(ref)
        if key not in records or key in selected:
            raise ValueError("selected: duplicate or unexamined reference")
        selected.add(key)

    _list(payload["analyses"], "analyses")
    findings = []
    examined = set()
    for analysis in payload["analyses"]:
        _object(analysis, "uuid version relevant basis", "analysis")
        key = _reference(analysis)
        if key not in records or key in examined:
            raise ValueError("analysis: duplicate or unexamined reference")
        examined.add(key)
        if type(analysis["relevant"]) is not bool:
            raise ValueError("analysis.relevant must be boolean")
        basis = analysis["basis"]
        _object(basis, "classification author explanation quote", "basis")
        if basis["classification"] != "analysis" or basis["author"] not in ("caller", "skill"):
            raise ValueError("basis: relevance must be caller/skill analysis, not observation")
        _text(basis["explanation"], "basis.explanation")
        _text(basis["quote"], "basis.quote")
        record = records[key]
        if basis["quote"] not in record["statement"]:
            raise ValueError("basis.quote must occur exactly in the referenced record statement")
        requested = set(query["tags"])
        actual = set(record["tags"])
        matches = bool(requested & actual) if match_mode == "any" else requested <= actual
        if analysis["relevant"] and not matches:
            findings.append({
                "category": "near-miss-tag",
                "classification": "analysis",
                "scope": scope["name"],
                "memory": {field: record[field] for field in
                           ("uuid", "version", "status", "scopeDimension", "scopeIdentifier")},
                "proposedEvidence": record["status"] == "proposed",
                "observation": {"classification": "observation", "requestedTags": query["tags"],
                                "facetMatchMode": match_mode, "actualTags": record["tags"],
                                "exactTagMatch": False},
                "basis": basis,
            })

    return {
        "scope": scope,
        "originalQuery": query,
        "selected": payload["selected"],
        "disclosure": payload["disclosure"],
        "findings": sorted(findings, key=lambda f: (f["memory"]["uuid"], f["memory"]["version"])),
        "qualification": "Findings concern only supplied, approved examined evidence; none detected is not store-wide absence. "
                         "Evidence approval permits examination, not canon: proposed evidence remains proposed. "
                         "Record applicability is its declared scope, not the examined-set name or query scope. "
                         "Exact tag mismatch is observed; relevance is caller/skill analysis, not established synonymy. "
                         "Tag relationships were not followed. Selection is unchanged. Caps and non-tag filters may "
                         "exclude records independently; absence does not prove tag-caused exclusion or unseen missing records.",
        "limits": {"maxBytes": MAX_BYTES, "maxRecords": MAX_RECORDS, "maxTags": MAX_TAGS},
    }


def main():
    try:
        raw = sys.stdin.buffer.read(MAX_BYTES + 1)
        if len(raw) > MAX_BYTES:
            raise ValueError("input exceeds the byte cap; narrow the approved evidence explicitly")
        report = build_report(json.loads(raw))
    except (ValueError, TypeError, RecursionError):
        print("Invalid near-miss evidence: check schema, bounds, scope, references and analysis basis.", file=sys.stderr)
        return 1
    print(json.dumps(report, indent=2, sort_keys=True))
    return 0


if __name__ == "__main__":
    sys.exit(main())
