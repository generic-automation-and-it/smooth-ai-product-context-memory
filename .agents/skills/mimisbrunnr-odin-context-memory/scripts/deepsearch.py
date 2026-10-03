#!/usr/bin/env python3
"""Bounded opt-in recall expansion for delegated memory agents."""

import json
import sys
import time
import uuid

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


def execute(payload, request=client._request, clock=time.monotonic):
    if not isinstance(payload, dict) or not isinstance(payload.get("baseline"), dict):
        raise ValueError("deepsearch requires a baseline query object")

    # One foreground deadline for the whole command. `recall_deadline` refuses an out-of-range setting
    # rather than defaulting it, and config may only shorten the cap. It is checked before every pass
    # and is the per-pass socket budget, so a hung store cannot stretch a ten-call chain into ten
    # separate timeouts — which is the difference between a bounded recall and a five-minute one.
    deadline = client.recall_deadline()
    started = clock()

    def remaining():
        return deadline - (clock() - started)

    # Two distinct facts that were once one flag. `stopped_early` is "a pass did not complete"; a pass
    # that hung (`timed-out`) and a pass that never started because the wall clock was spent
    # (`budget_exhausted`) are different — reporting the first as "the budget ran out" tells the agent to
    # wait when the store is simply slow.
    stopped_early = False
    budget_exhausted = False

    def run_pass(kind, value, limit, method, path, body):
        """Run one pass under the deadline. Returns `(response, status)`.

        `status` is `completed`, `timed-out` (the store accepted the connection and did not answer
        within the budget) or `not-run` (the wall clock was already spent). A timeout stops the chain
        rather than aborting it: the passes already completed are kept and the rest are disclosed,
        because a hang that discards every completed pass is the outcome the deadline exists to
        prevent. Whole passes only — a pass contributes all of its records or none.
        """
        nonlocal stopped_early, budget_exhausted
        if stopped_early:
            return None, "not-run"
        if remaining() <= 0:
            stopped_early = True
            budget_exhausted = True
            return None, "not-run"
        budget = max(0.001, min(client.HTTP_TIMEOUT, remaining()))
        try:
            return request(method, path, body, timeout=budget), "completed"
        except client.ClientError as error:
            if error.status_text != "timed-out":
                raise
            stopped_early = True
            return None, "timed-out"

    baseline = dict(payload["baseline"])
    baseline["limit"] = BASELINE_LIMIT
    baseline.setdefault("includeProposed", True)
    baseline.setdefault("currentOnly", True)
    baseline.setdefault("recallPurpose", "direct_retrieval")
    baseline.setdefault("callerRequestId", str(uuid.uuid4()))

    keywords = payload.get("keywords", [])
    if not isinstance(keywords, list):
        raise ValueError("keywords must be an array")
    if not all(isinstance(word, str) for word in keywords):
        raise ValueError("keywords must be an array of strings")
    keywords = sorted({word.strip() for word in keywords if isinstance(word, str) and word.strip()})
    if any(len(word.split()) > 3 for word in keywords):
        raise ValueError("each keyword query may contain at most three lexemes")

    scope = baseline.get("scopeDimension")
    has_context_selector = baseline.get("groupUuid") is not None \
        or baseline.get("ticketProvider") is not None or baseline.get("ticketKey") is not None
    traversal_skipped_for_context = has_context_selector and not scope

    merged = []
    seen = set()
    passes = []
    keyword_plan = keywords[:MAX_KEYWORDS]

    def mark_not_run(kind, values, limit):
        for value in values:
            passes.append(_disclosure(kind, value, limit, [], 0, "not-run"))

    response, status = run_pass("baseline", None, BASELINE_LIMIT, "POST",
                                "/api/context/query", baseline)
    baseline_completed = status == "completed"
    baseline_rows = response.get("items", []) if response is not None else []
    added = _add(baseline_rows, merged, seen, AGGREGATE_LIMIT)
    passes.append(_disclosure("baseline", None, BASELINE_LIMIT, baseline_rows, added, status))

    eligible_anchors = [row.get("uuid") for row in baseline_rows if row.get("uuid")]

    if not baseline_completed:
        # Traversal anchors come from the baseline rows, so none can be named when the baseline itself
        # did not answer; the keyword plan is known without it and is named instead.
        mark_not_run("keyword", keyword_plan, KEYWORD_LIMIT)
    else:
        for index, keyword in enumerate(keyword_plan):
            query = dict(baseline)
            query.update(query=keyword, facets=[], tags=[], kind=None, limit=KEYWORD_LIMIT)
            response, status = run_pass("keyword", keyword, KEYWORD_LIMIT, "POST",
                                        "/api/context/query", query)
            rows = response.get("items", []) if response is not None else []
            added = _add(rows, merged, seen, AGGREGATE_LIMIT)
            passes.append(_disclosure("keyword", keyword, KEYWORD_LIMIT, rows, added, status))
            if status != "completed":
                mark_not_run("keyword", keyword_plan[index + 1:], KEYWORD_LIMIT)
                break
            if len(merged) >= AGGREGATE_LIMIT:
                break

        traversal_plan = [] if traversal_skipped_for_context else eligible_anchors[:MAX_TRAVERSALS]
        for index, anchor in enumerate(traversal_plan):
            path_request = {
                "sourceUuid": anchor,
                "maxDepth": 1,
                "direction": "either",
                "limit": TRAVERSAL_LIMIT,
            }
            if scope:
                path_request["scopeDimension"] = scope
            response, status = run_pass("traversal", anchor, TRAVERSAL_LIMIT, "POST",
                                        "/api/context/paths", path_request)
            rows = [path.get("endpoint", {}) for path in response.get("paths", [])] \
                if response is not None else []
            added = _add(rows, merged, seen, AGGREGATE_LIMIT)
            passes.append(_disclosure("traversal", anchor, TRAVERSAL_LIMIT, rows, added, status))
            if status != "completed":
                mark_not_run("traversal", traversal_plan[index + 1:], TRAVERSAL_LIMIT)
                break
            if len(merged) >= AGGREGATE_LIMIT:
                break

    keywords_executed = sum(1 for item in passes if item["kind"] == "keyword"
                            and item["status"] == "completed")
    keywords_omitted = len(keywords) - keywords_executed
    anchors_executed = sum(1 for item in passes if item["kind"] == "traversal"
                           and item["status"] == "completed")
    anchors_omitted = len(eligible_anchors) - anchors_executed
    passes_incomplete = [{"kind": item["kind"], "value": item["value"]}
                         for item in passes if item["status"] != "completed"]

    return {
        "items": merged,
        "disclosure": {
            "mode": "deepsearch",
            "uniqueCandidates": len(merged),
            "aggregateLimit": AGGREGATE_LIMIT,
            "aggregateLimitReached": len(merged) >= AGGREGATE_LIMIT,
            "deadlineSeconds": deadline,
            # `stoppedEarly` is any early stop; `budgetExhausted` narrows it to the wall clock. A pass
            # that hung reports `stoppedEarly` without `budgetExhausted`, so a slow store is not
            # mislabelled as an exhausted budget — the two call for different responses.
            "stoppedEarly": stopped_early,
            "budgetExhausted": budget_exhausted,
            "keywordsRequested": len(keywords),
            "keywordsExecuted": keywords_executed,
            "keywordsOmittedByCap": keywords_omitted,
            # `null` when the baseline never answered: the traversal set was never enumerated, so a `0`
            # would read as "nothing to traverse" rather than "unknown".
            "anchorsEligible": len(eligible_anchors) if baseline_completed else None,
            "anchorsExecuted": anchors_executed,
            "anchorsOmittedByCap": anchors_omitted if baseline_completed else None,
            "traversalSkippedForContextSelector": traversal_skipped_for_context,
            "passes": passes,
            "passesIncomplete": passes_incomplete,
            "possiblyOmitted": len(merged) >= AGGREGATE_LIMIT
                or keywords_omitted > 0 or anchors_omitted > 0 or traversal_skipped_for_context
                or any(item["limitReached"] for item in passes)
                or stopped_early,
        },
    }


def _disclosure(kind, value, limit, rows, added, status="completed"):
    return {
        "kind": kind,
        "value": value,
        "limit": limit,
        "returned": len(rows),
        "uniqueAdded": added,
        "limitReached": len(rows) >= limit,
        "status": status,
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
