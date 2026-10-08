#!/usr/bin/env python3
"""Reproducible structural cost measurement; never emits memory content."""

import json

FACTS = 10
# The capture client's per-`set` item cap (`MAX_CANDIDATES` in context_memory_client.py).
SET_ITEM_CAP = 20
# Content-bearing items one fact can put in a `set`: an authority resolution won by the existing claim
# writes the candidate's losing version and then restores the existing winner as a second version
# (authority.py). A divergence adds a record with no content, so it adds no blob.
CONTENT_ITEMS_PER_FACT_MAXIMUM = 2
# `SetMemories.PersistAsync` makes one `IBlobStorage.StoreAsync` call per content-bearing item, and
# `S3BlobStorage.StoreAsync` turns each call into object-store requests:
#   bytes already stored   StatObject                                     1
#   new bytes              StatObject, BucketExists, PutObject            3
#   bucket missing         + MakeBucket, and a BucketExists re-check
#                          when MakeBucket loses a creation race          +2, once per `set`
# The earlier figure counted store calls and called them operations, so it read as 20 where the
# object store can see 62 (issue 184).
REQUESTS_PER_NEW_BODY = 3
BUCKET_CREATION_REQUESTS = 2
# The MinIO client is handed an `IHttpClientFactory` client under the Host's
# `AddStandardResilienceHandler`, whose retry strategy allows 3 retries per request by default — for
# PUT too — so a transient fault can repeat each request up to four times on the wire.
HTTP_ATTEMPTS_PER_REQUEST = 4
BASELINE_CANDIDATES = 200
LOOKUP_SURFACED = 5
GROUNDING_SURFACED = 20


def blob_bounds(facts):
    """Upper bounds for one `set` of `facts` atomic facts; dry run makes none of these calls."""
    store_calls = min(facts * CONTENT_ITEMS_PER_FACT_MAXIMUM, SET_ITEM_CAP)
    requests = store_calls * REQUESTS_PER_NEW_BODY + (BUCKET_CREATION_REQUESTS if store_calls else 0)
    return {
        "blobStoreCallsMaximum": store_calls,
        "blobUploadsMaximum": store_calls,
        "objectStoreRequestsMaximum": requests,
        "objectStoreHttpAttemptsMaximum": requests * HTTP_ATTEMPTS_PER_REQUEST,
    }


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
            **blob_bounds(FACTS),
        },
        "dryRun": {
            "agentInvocations": 1,
            "logicalSummaryJudgements": FACTS,
            "logicalKeywordJudgements": FACTS,
            "httpCallsMinimum": 3,
            "optionalDeepsearchPasses": 0,
            "blobStoreCalls": 0,
            "objectStoreRequests": 0,
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
