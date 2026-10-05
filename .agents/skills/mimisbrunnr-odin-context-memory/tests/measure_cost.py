#!/usr/bin/env python3
"""Reproducible structural cost measurement; never emits memory content."""

import json

FACTS = 10
# The capture client's per-`set` item cap (`MAX_CANDIDATES` in context_memory_client.py).
SET_ITEM_CAP = 20
# Items one fact can put in a `set`: an authority resolution won by the existing claim writes the
# candidate's losing version and then restores the existing winner as a second version (authority.py),
# and the Host makes one blob store call per item that carries content — an existence check, plus an
# upload only when the bytes are new — so this is an upper bound on store operations, not on uploads.
# A divergence adds a record with no content, so it adds no blob.
ITEMS_PER_FACT_MAXIMUM = 2
BASELINE_CANDIDATES = 200
LOOKUP_SURFACED = 5
GROUNDING_SURFACED = 20


def encoded_size(rows):
    return len(json.dumps(rows, separators=(",", ":")).encode("utf-8"))


def measure():
    raw_rows = [
        {
            "uuid": f"00000000-0000-4000-8000-{index:012d}",
            "version": 1,
            "description": "x" * 80,
            "statement": "x" * 160,
            "contentSummary": "x" * 120,
            "kind": "decision",
            "status": "approved",
            "scopeDimension": "product",
        }
        for index in range(BASELINE_CANDIDATES)
    ]
    lookup = {"answer": "x" * 300, "citations": raw_rows[:LOOKUP_SURFACED], "alsoInStore": 195}
    grounding = {"brief": "x" * 4000, "citations": raw_rows[:GROUNDING_SURFACED], "alsoInStore": 180}

    result = {
        "fixtureFacts": FACTS,
        "normalWrite": {
            "agentInvocations": 1,
            "logicalSummaryJudgements": FACTS,
            "logicalKeywordJudgements": FACTS,
            "httpCallsMinimum": 3,
            "optionalDeepsearchPasses": 0,
            "blobWritesMaximum": min(FACTS * ITEMS_PER_FACT_MAXIMUM, SET_ITEM_CAP),
        },
        "dryRun": {
            "agentInvocations": 1,
            "logicalSummaryJudgements": FACTS,
            "logicalKeywordJudgements": FACTS,
            "httpCallsMinimum": 3,
            "optionalDeepsearchPasses": 0,
            "blobWrites": 0,
        },
        "deepsearch": {
            "agentInvocations": 1,
            "baselinePasses": 1,
            "keywordPassesMaximum": 4,
            "traversalPassesMaximum": 5,
            "aggregateCandidateCap": 400,
        },
        "mainContextBytes": {
            "raw200RowsBefore": encoded_size(raw_rows),
            "lookupAfter": encoded_size(lookup),
            "groundingAfter": encoded_size(grounding),
        },
        "providerInvocations": "unavailable",
        "inputTokens": "unavailable",
        "outputTokens": "unavailable",
        "note": "Structural estimates only: counts are contract-derived bounds; byte sizes use deterministic synthetic content, not stored memory or live workflows.",
    }
    return result


def main():
    print(json.dumps(measure(), indent=2))


if __name__ == "__main__":
    main()
