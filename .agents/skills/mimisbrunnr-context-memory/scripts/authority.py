#!/usr/bin/env python3
"""Compose version writes for a rule-resolvable disagreement."""

import copy
import json
import sys

ALLOWED_AUTHORITIES = {"shipped_behavior", "agreed_vocabulary"}


def _version(write, target_uuid):
    item = copy.deepcopy(write)
    item["uuid"] = target_uuid
    item.pop("createUuid", None)
    return item


def compose(payload):
    if not isinstance(payload, dict):
        raise ValueError("authority input must be an object")
    authority = payload.get("authority")
    if authority not in ALLOWED_AUTHORITIES:
        raise ValueError("authority must be shipped_behavior or agreed_vocabulary")
    winner = payload.get("winner")
    existing = payload.get("existing")
    if winner not in ("candidate", "existing"):
        raise ValueError("winner must be candidate or existing")
    if not isinstance(existing, dict):
        raise ValueError("existing claim is required")
    target_uuid = existing.get("uuid")
    candidate_write = payload.get("candidateWrite")
    existing_write = existing.get("write")
    if not target_uuid or not isinstance(candidate_write, dict) or not isinstance(existing_write, dict):
        raise ValueError("existing uuid plus candidate and existing writes are required")

    if winner == "candidate":
        items = [_version(candidate_write, target_uuid)]
    else:
        items = [_version(candidate_write, target_uuid), _version(existing_write, target_uuid)]

    return {
        "items": items,
        "links": [],
        "authority": authority,
        "winner": winner,
        "losingPositionRetained": True,
    }


def main():
    try:
        result = compose(json.load(sys.stdin))
    except ValueError as error:
        print(f"Invalid authority judgement: {error}", file=sys.stderr)
        return 1
    print(json.dumps(result, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
