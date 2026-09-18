#!/usr/bin/env python3
"""Render ordinary set items/links from explicit genuine-conflict judgements."""

import json
import sys
import uuid

DIVERGENCE_NAMESPACE = uuid.UUID("d2146dfa-da0b-4d50-96b7-ea075991006d")
AUTO_SOURCE = "mimisbrunnr:auto-divergence:v1"


def pair_key(left_uuid, left_version, right_uuid, right_version):
    refs = sorted((f"{left_uuid}:{left_version}", f"{right_uuid}:{right_version}"))
    return "|".join(refs)


def divergence_uuid(key):
    return str(uuid.uuid5(DIVERGENCE_NAMESPACE, key))


def existing_pairs(records):
    pairs = set()
    for record in records:
        if record.get("kind") != "divergence" or record.get("status") != "proposed":
            continue
        references = [source.get("reference") for source in record.get("sources", [])
                      if source.get("kind") == "memory"]
        if len(references) != 2:
            continue
        normalized = []
        for reference in references:
            if not isinstance(reference, str) or ":v" not in reference:
                normalized = []
                break
            memory_uuid, version = reference.rsplit(":v", 1)
            if not version.isdigit():
                normalized = []
                break
            normalized.append(f"{memory_uuid}:{int(version)}")
        if len(normalized) == 2:
            pairs.add("|".join(sorted(normalized)))
    return pairs


def existing_record_uuid(records, key):
    expected = divergence_uuid(key)
    return any(record.get("uuid") == expected and record.get("kind") == "divergence"
               for record in records)


def compose(payload):
    if not isinstance(payload, dict):
        raise ValueError("divergence input must be an object")
    candidate = payload.get("candidate")
    existing = payload.get("existing")
    if not isinstance(candidate, dict) or not isinstance(existing, dict):
        raise ValueError("candidate and existing claim references are required")
    candidate_write = candidate.get("write")
    if not isinstance(candidate_write, dict):
        raise ValueError("candidate.write is required")
    if candidate_write.get("kind") == "divergence" or existing.get("kind") == "divergence":
        raise ValueError("divergence records cannot become conflict evidence")
    if payload.get("sameSubject") is not True:
        raise ValueError("genuine conflict requires an explicit same-subject judgement")
    if candidate.get("scopeDimension") != existing.get("scopeDimension") \
            or candidate.get("scopeIdentifier") != existing.get("scopeIdentifier"):
        raise ValueError("claims from different applicability scopes are not a genuine conflict")

    candidate_uuid = candidate_write.get("createUuid")
    existing_uuid = existing.get("uuid")
    if not candidate_uuid or not existing_uuid:
        raise ValueError("candidate createUuid and existing uuid are required")
    key = pair_key(existing_uuid, existing.get("version"), candidate_uuid, 1)
    known = set(payload.get("existingPairs", []))
    known.update(existing_pairs(payload.get("existingDivergences", [])))
    records = payload.get("existingDivergences", [])
    if key in known or key in existing_pairs(records) or existing_record_uuid(records, key):
        return {"items": [], "links": [], "diverged": 0, "pair": key}

    record_uuid = divergence_uuid(key)
    reason = payload.get("reason")
    if not isinstance(reason, str) or not reason.strip():
        raise ValueError("a non-empty conflict reason is required")
    now = candidate_write.get("validFrom")
    candidate_write = dict(candidate_write)
    candidate_write["description"] = f"Unresolved alternative to {existing_uuid} ({candidate_uuid})"
    divergence = {
        "uuid": None,
        "createUuid": record_uuid,
        "name": "Unresolved divergence",
        "description": f"Unresolved conflict {record_uuid}",
        "statement": reason,
        "contentSummary": "Two same-scope claims conflict and no stated authority selects a winner.",
        "kind": "divergence",
        "facets": list(candidate_write.get("facets", [])),
        "tags": list(candidate_write.get("tags", [])),
        "status": "proposed",
        "confidence": min(int(candidate_write.get("confidence", 0)), int(existing.get("confidence", 0))),
        "content": None,
        "sources": [
            {"kind": "system", "reference": AUTO_SOURCE, "capturedAt": now},
            {"kind": "memory", "reference": f"{existing_uuid}:v{existing.get('version')}", "capturedAt": now},
            {"kind": "memory", "reference": f"{candidate_uuid}:v1", "capturedAt": now},
        ],
        "validFrom": now,
        "validUntil": None,
        "summaryModel": candidate_write.get("summaryModel"),
        "summaryPromptVersion": candidate_write.get("summaryPromptVersion"),
    }
    links = [
        {"sourceUuid": record_uuid, "targetUuid": existing_uuid,
         "relation": "contradicts", "reason": reason},
        {"sourceUuid": record_uuid, "targetUuid": candidate_uuid,
         "relation": "contradicts", "reason": reason},
    ]
    return {"items": [candidate_write, divergence], "links": links, "diverged": 1, "pair": key}


def main():
    try:
        result = compose(json.load(sys.stdin))
    except (ValueError, TypeError) as error:
        print(f"Invalid divergence judgement: {error}", file=sys.stderr)
        return 1
    print(json.dumps(result, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
