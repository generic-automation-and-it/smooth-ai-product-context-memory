#!/usr/bin/env python3
"""Deterministic bundle detector for the atomicity stage of the mimisbrunnr-context-memory skill.

One memory is one atomic fact. This helper scores a candidate's statement for signals that it bundles
several independent claims (clause junctions, list/tally patterns), returning a SIMPLE / BUNDLED
verdict. Reason clauses and noun-phrase coordination are not such signals. It is the machine-testable
core of the atomicity stage; the final decision to split vs skip, and the routing of the
unprocessable remainder, stays in SKILL.md (LADR-002).

Conservative on purpose: a candidate is only flagged BUNDLED on strong signals, so it never
over-splits a genuinely single fact.
"""

import argparse
import json
import re
import sys

# Junction signals that a single record likely asserts more than one *independent* fact.
#
# Deliberately excluded, because each marks one fact rather than two:
#   - reason clauses (because / since / so that / therefore / thus) — a fact plus the reason it holds
#   - relative clauses (which) — a fact plus a qualifier on its own subject
#   - bare "and" joining noun phrases ("typed relations and JSONB", "local-first and private")
# Scoring those fired on 12/12 candidates of a real braindump batch, including every candidate the
# detector then called simple: a signal that never discriminates cannot support a verdict.
# "and" counts only after a comma, the cheapest deterministic proxy for clause coordination.
#
# Two tiers, because the markers are not equally strong. A semicolon or whereas/however
# cannot join anything but two finite clauses, so one occurrence already means two claims;
# "but"/"while" usually do too (temporal "while" and "not X but Y" are accepted misses);
# an additive adverb can sit inside a single clause, so it takes two.
_CONTRASTIVE = [
    r"\b(?:but|however|whereas|while)\b",
    r";",
]
_ADDITIVE = [
    r"\b(?:also|furthermore|moreover|additionally)\b",
    r",\s*and\b",
    r"\b(?:that explains|also means)\b",
    # "both X and Y" is usually one fact about a coordinated pair ("for both capture and
    # retrieval"), so it contributes rather than deciding on its own.
    r"\bboth\b",
]
_TALLY = [
    # Leading negative lookbehind (not \b) so "local-first" or "v1-first" does not count "first"
    # as an enumerator — a common false positive in product phrasing.
    r"(?<![-\w])(?:first|second|third|fourth|fifth|finally|lastly)\b",
    r"(?:\b\d+\b|\b(?:two|three|four|five|several|many))\s+(?:things?|points?|reasons?|facts?)\b",
    r"\b(?:all of|each of)\b",
]
_SPLIT = [
    r"\b(?:this|that|the latter|the former)\b",
    r"\b(?:respectively|separately|independently)\b",
]

_COMPILED_CONTRASTIVE = [re.compile(p, re.IGNORECASE) for p in _CONTRASTIVE]
_COMPILED_ADDITIVE = [re.compile(p, re.IGNORECASE) for p in _ADDITIVE]
_COMPILED_TALLY = [re.compile(p, re.IGNORECASE) for p in _TALLY]
_COMPILED_SPLIT = [re.compile(p, re.IGNORECASE) for p in _SPLIT]

# Count occurrence-based signals. Junctions are counted by match, not just by which pattern group
# fired — ", and ... also ... but" is three independent claim junctions.
TALLY_THRESHOLD = 1
DISCOURSE_THRESHOLD = 2
CONTRASTIVE_WEIGHT = 2


def _count_matches(patterns, text):
    total = 0
    for pattern in patterns:
        total += len(pattern.findall(text))
    return total


def classify(text):
    """Return {'verdict': 'simple'|'bundled', 'signals': ['tally'|'discourse'|'split-ref', ...]}.

    Conservatively flags only strong multi-fact signals; a lone "and" between attributes, and a
    reason clause explaining one fact, are not bundles. This is a detector for the atomicity stage,
    not the decision.
    """
    text = text or ""
    tally = _count_matches(_COMPILED_TALLY, text)
    discourse = (CONTRASTIVE_WEIGHT * _count_matches(_COMPILED_CONTRASTIVE, text)
                 + _count_matches(_COMPILED_ADDITIVE, text))
    split_ref = _count_matches(_COMPILED_SPLIT, text)

    signals = []
    if tally:
        signals.append("tally")
    if discourse:
        signals.append("discourse")
    if split_ref:
        signals.append("split-ref")

    # Bundled when there is an enumerator/tally, or when the weighted junction score suggests several
    # independent claims: one contrastive clause junction, or two additive ones.
    if tally >= TALLY_THRESHOLD or discourse >= DISCOURSE_THRESHOLD:
        verdict = "bundled"
    else:
        verdict = "simple"

    return {"verdict": verdict, "signals": signals}


def main():
    parser = argparse.ArgumentParser(prog="atomicity")
    parser.add_argument("--input", help="JSON file of candidate {description,statement} objects; defaults to stdin")
    args = parser.parse_args()

    if args.input:
        with open(args.input, "r", encoding="utf-8") as fh:
            batch = json.load(fh)
    else:
        batch = json.load(sys.stdin)

    if not isinstance(batch, list):
        print("atomicity: expected a JSON array on stdin", file=sys.stderr)
        sys.exit(1)

    results = []
    for index, item in enumerate(batch):
        if not isinstance(item, dict):
            print(f"atomicity: item {index} must be an object", file=sys.stderr)
            sys.exit(1)
        # The statement is the claim; the description is only its subject label, and coordination
        # inside a subject ("Controlled labels and free tags") is not a second fact. Scoring both
        # inflated every count. Fall back to the description only when no statement was supplied.
        text = item.get("statement") or item.get("description", "")
        verdict = classify(text)
        results.append({"candidate_index": index, **verdict})

    print(json.dumps({"results": results}, indent=2))


if __name__ == "__main__":
    main()
