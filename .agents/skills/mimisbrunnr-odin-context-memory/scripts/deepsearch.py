#!/usr/bin/env python3
"""Bounded opt-in recall expansion for delegated memory agents."""

import json
import sys
import time

import context_memory_client as client

BASELINE_LIMIT = 200
KEYWORD_LIMIT = 25
MAX_KEYWORDS = 4
TRAVERSAL_LIMIT = 20
MAX_TRAVERSALS = 5
AGGREGATE_LIMIT = 400


def _key(item):
    return item.get("uuid"), item.get("version")


def _rows(response, field, endpoint=False):
    """The rows of one answered pass, or None when the answer is not a complete page.

    The store always answers `{"items": [...]}` for a query and `{"paths": [{"endpoint": {...}}]}` for a
    traversal. An empty body, a non-object, a missing or non-list `items`/`paths`, a row that is not an
    object or has no `uuid`, and a path without an `endpoint` object are all an answer this client
    cannot read — and reading any of them as "no rows" reported a completed empty pass, which is the
    full-coverage claim an incomplete response must never make (issue 184). Whole passes only: a page
    with one unreadable row contributes nothing rather than the rows around it.
    """
    if not isinstance(response, dict) or not isinstance(response.get(field), list):
        return None
    rows = response[field]
    if endpoint:
        if not all(isinstance(path, dict) and isinstance(path.get("endpoint"), dict) for path in rows):
            return None
        rows = [path["endpoint"] for path in rows]
    # A row is identified by uuid **and** version: merging and dedupe key on both, so a versionless row
    # could not be placed and was counted as a completed pass all the same (issue 188).
    if not all(isinstance(row, dict) and isinstance(row.get("uuid"), str) and row["uuid"]
               and type(row.get("version")) is int and row["version"] > 0
               for row in rows):
        return None
    return rows


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
        """Run one pass under the deadline. Returns `(rows, status)`.

        `status` is `completed`, `timed-out` (the store accepted the connection and did not answer
        within the budget), `not-run` (the wall clock was already spent), `malformed` (an answer that
        is not a complete page — see `_rows`) or, for a traversal only, `forbidden`. `rows` is the
        page's rows for a completed pass and empty otherwise. A timeout stops the chain rather than
        aborting it: the passes already completed are kept and the rest are disclosed, because a hang
        that discards every completed pass is the outcome the deadline exists to prevent. Whole passes
        only — a pass contributes all of its records or none.

        A traversal the store refuses with 403 — the anchor's group sits outside the requested scope,
        which a group or ticket selector makes possible because the baseline ignores scope there — is
        one unreadable anchor, not a failed recall. It is disclosed and the chain continues; raising
        here discarded the completed baseline along with it. A `malformed` pass is disclosed the same
        way and does not stop the chain: the store answered, so the deadline is not the problem.
        """
        nonlocal stopped_early, budget_exhausted
        if stopped_early:
            return [], "not-run"
        if remaining() <= 0:
            stopped_early = True
            budget_exhausted = True
            return [], "not-run"
        budget = max(0.001, min(client.HTTP_TIMEOUT, remaining()))
        try:
            response = request(method, path, body, timeout=budget)
        except client.ClientError as error:
            if kind == "traversal" and error.status == 403:
                return [], "forbidden"
            if error.status_text != "timed-out":
                raise
            stopped_early = True
            return [], "timed-out"
        except ValueError:
            # A body cut off mid-JSON. The client raises rather than guessing, and here that is one
            # unreadable page, not grounds to discard the passes already completed.
            return [], "malformed"
        rows = _rows(response, "paths" if kind == "traversal" else "items",
                     endpoint=kind == "traversal")
        return ([], "malformed") if rows is None else (rows, "completed")

    baseline = dict(payload["baseline"])
    baseline["limit"] = BASELINE_LIMIT
    baseline.setdefault("includeProposed", True)
    baseline.setdefault("currentOnly", True)

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
    # Distinct, because one endpoint linked from several anchors is one memory outside the selection.
    endpoints_outside_selector = set()

    def mark_not_run(kind, values, limit):
        for value in values:
            passes.append(_disclosure(kind, value, limit, [], 0, "not-run"))

    baseline_rows, status = run_pass("baseline", None, BASELINE_LIMIT, "POST",
                                     "/api/context/query", baseline)
    baseline_completed = status == "completed"
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
            rows, status = run_pass("keyword", keyword, KEYWORD_LIMIT, "POST",
                                    "/api/context/query", query)
            added = _add(rows, merged, seen, AGGREGATE_LIMIT)
            passes.append(_disclosure("keyword", keyword, KEYWORD_LIMIT, rows, added, status))
            if status not in ("completed", "malformed"):
                mark_not_run("keyword", keyword_plan[index + 1:], KEYWORD_LIMIT)
                break
            if len(merged) >= AGGREGATE_LIMIT:
                break

        # A group or ticket selector narrows the baseline, but `/paths` takes no selector, so its
        # endpoints can sit in any group of the requested scope. Only endpoints inside the selected
        # group(s) are kept; the rest are counted, never merged, so traversal widens along links
        # without widening past what the caller selected.
        selected_groups = {row.get("groupUuid") for row in baseline_rows if row.get("groupUuid")}
        if baseline.get("groupUuid") is not None:
            selected_groups.add(baseline["groupUuid"])
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
            rows, status = run_pass("traversal", anchor, TRAVERSAL_LIMIT, "POST",
                                    "/api/context/paths", path_request)
            kept = rows
            if has_context_selector:
                kept = [row for row in rows if row.get("groupUuid") in selected_groups]
                endpoints_outside_selector.update(_key(row) for row in rows
                                                  if row.get("groupUuid") not in selected_groups)
            added = _add(kept, merged, seen, AGGREGATE_LIMIT)
            passes.append(_disclosure("traversal", anchor, TRAVERSAL_LIMIT, rows, added, status))
            if status in ("forbidden", "malformed"):
                continue
            if status != "completed":
                mark_not_run("traversal", traversal_plan[index + 1:], TRAVERSAL_LIMIT)
                break
            if len(merged) >= AGGREGATE_LIMIT:
                break

    def count(kind, status):
        return sum(1 for item in passes if item["kind"] == kind and item["status"] == status)

    def recorded(kind):
        return sum(1 for item in passes if item["kind"] == kind)

    keywords_executed = count("keyword", "completed")
    # Omitted by a cap = never given a pass at all: past MAX_KEYWORDS/MAX_TRAVERSALS, or left when the
    # aggregate limit filled. Every pass that *was* recorded — completed, malformed, forbidden, or
    # timed out / not run after a failure — is counted under its own status and listed in
    # `passesIncomplete`; subtracting only some statuses counted a failed recall as a cap omission
    # (issue 190 #10). A traversal skipped for a context selector is not a cap omission either: it has
    # its own flag.
    keywords_omitted = len(keywords) - recorded("keyword")
    anchors_executed = count("traversal", "completed")
    anchors_forbidden = count("traversal", "forbidden")
    anchors_omitted = 0 if traversal_skipped_for_context else len(eligible_anchors) - recorded("traversal")
    passes_malformed = sum(1 for item in passes if item["status"] == "malformed")
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
            # Anchors the store refused to traverse (403), each also listed in `passesIncomplete`.
            "anchorsForbidden": anchors_forbidden,
            # Distinct path endpoints dropped because they sit outside the group/ticket selector.
            # Deliberately not part of `possiblyOmitted`: they were never in the selection, so nothing
            # selected is missing — but the count says the links exist.
            "endpointsOutsideSelector": len(endpoints_outside_selector),
            # Passes the store answered with something other than a complete page — an empty body, a
            # missing or non-list `items`/`paths`, a row without a `uuid`, a body cut off mid-JSON.
            # Each contributed nothing and is listed in `passesIncomplete`; never a completed empty pass.
            "passesMalformed": passes_malformed,
            "passes": passes,
            "passesIncomplete": passes_incomplete,
            "possiblyOmitted": len(merged) >= AGGREGATE_LIMIT
                or keywords_omitted > 0 or anchors_omitted > 0 or anchors_forbidden > 0
                or passes_malformed > 0
                or traversal_skipped_for_context
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
