#!/usr/bin/env python3
"""Bounded opt-in recall expansion for delegated memory agents."""

import json
import sys

import context_memory_client as client

BASELINE_LIMIT = 200
KEYWORD_LIMIT = 25
MAX_KEYWORDS = 4
TRAVERSAL_LIMIT = 20
MAX_TRAVERSALS = 5
AGGREGATE_LIMIT = 400


def _key(item):
    return item.get("uuid"), item.get("version")


def _add(items, merged, seen, remaining):
    added = 0
    for item in items:
        key = _key(item)
        if key in seen:
            continue
        if len(merged) >= remaining:
            break
        seen.add(key)
        merged.append(item)
        added += 1
    return added


def execute(payload, request=client._request):
    if not isinstance(payload, dict) or not isinstance(payload.get("baseline"), dict):
        raise ValueError("deepsearch requires a baseline query object")

    baseline = dict(payload["baseline"])
    baseline["limit"] = BASELINE_LIMIT
    baseline.setdefault("includeProposed", True)
    baseline.setdefault("currentOnly", True)
    baseline_rows = request("POST", "/api/context/query", baseline).get("items", [])

    merged = []
    seen = set()
    passes = []
    added = _add(baseline_rows, merged, seen, AGGREGATE_LIMIT)
    passes.append(_disclosure("baseline", None, BASELINE_LIMIT, baseline_rows, added))

    keywords = payload.get("keywords", [])
    if not isinstance(keywords, list):
        raise ValueError("keywords must be an array")
    keywords = sorted({word.strip() for word in keywords if isinstance(word, str) and word.strip()})
    if any(len(word.split()) > 3 for word in keywords):
        raise ValueError("each keyword query may contain at most three lexemes")

    omitted_keywords = max(0, len(keywords) - MAX_KEYWORDS)
    for keyword in keywords[:MAX_KEYWORDS]:
        query = dict(baseline)
        query.update(query=keyword, facets=[], tags=[], kind=None, limit=KEYWORD_LIMIT)
        rows = request("POST", "/api/context/query", query).get("items", [])
        added = _add(rows, merged, seen, AGGREGATE_LIMIT)
        passes.append(_disclosure("keyword", keyword, KEYWORD_LIMIT, rows, added))
        if len(merged) >= AGGREGATE_LIMIT:
            break

    scope = baseline.get("scopeDimension")
    has_context_selector = baseline.get("groupUuid") is not None \
        or baseline.get("ticketProvider") is not None or baseline.get("ticketKey") is not None
    eligible_anchors = [row.get("uuid") for row in baseline_rows if row.get("uuid")]
    traversal_skipped_for_context = has_context_selector and not scope
    anchors = [] if traversal_skipped_for_context else eligible_anchors[:MAX_TRAVERSALS]
    omitted_anchors = max(0, len(eligible_anchors) - len(anchors))
    for anchor in anchors:
        if len(merged) >= AGGREGATE_LIMIT:
            break
        path_request = {
            "sourceUuid": anchor,
            "maxDepth": 1,
            "direction": "either",
            "limit": TRAVERSAL_LIMIT,
        }
        if scope:
            path_request["scopeDimension"] = scope
        response = request("POST", "/api/context/paths", path_request)
        rows = [path.get("endpoint", {}) for path in response.get("paths", [])]
        added = _add(rows, merged, seen, AGGREGATE_LIMIT)
        passes.append(_disclosure("traversal", anchor, TRAVERSAL_LIMIT, rows, added))

    return {
        "items": merged,
        "disclosure": {
            "mode": "deepsearch",
            "uniqueCandidates": len(merged),
            "aggregateLimit": AGGREGATE_LIMIT,
            "aggregateLimitReached": len(merged) >= AGGREGATE_LIMIT,
            "keywordsRequested": len(keywords),
            "keywordsExecuted": min(len(keywords), MAX_KEYWORDS),
            "keywordsOmittedByCap": omitted_keywords,
            "anchorsEligible": len([row for row in baseline_rows if row.get("uuid")]),
            "anchorsExecuted": len(anchors),
            "anchorsOmittedByCap": omitted_anchors,
            "traversalSkippedForContextSelector": traversal_skipped_for_context,
            "passes": passes,
            "possiblyOmitted": len(merged) >= AGGREGATE_LIMIT
                or omitted_keywords > 0 or omitted_anchors > 0 or traversal_skipped_for_context
                or any(item["limitReached"] for item in passes),
        },
    }


def _disclosure(kind, value, limit, rows, added):
    return {
        "kind": kind,
        "value": value,
        "limit": limit,
        "returned": len(rows),
        "uniqueAdded": added,
        "limitReached": len(rows) >= limit,
    }


def main():
    try:
        result = execute(json.load(sys.stdin))
    except (ValueError, client.ClientError) as error:
        print(f"Deep search failed: {error}", file=sys.stderr)
        return 1
    print(json.dumps(result, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
