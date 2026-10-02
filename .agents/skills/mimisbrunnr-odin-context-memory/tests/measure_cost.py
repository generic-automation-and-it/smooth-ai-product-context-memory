#!/usr/bin/env python3
"""Reproducible structural cost measurement; never emits memory content."""

import json

FACTS = 10
BASELINE_CANDIDATES = 200
LOOKUP_SURFACED = 5
GROUNDING_SURFACED = 20


def encoded_size(rows):
    return len(json.dumps(rows, separators=(",", ":")).encode("utf-8"))


def main():
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
            "blobWritesMaximum": FACTS,
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
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
