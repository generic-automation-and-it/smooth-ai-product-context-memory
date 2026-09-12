#!/usr/bin/env python3
"""Deterministic bundle detector for the atomicity stage of the context-memory skill.

One memory is one atomic fact. This helper scores a candidate for signals that it bundles several
facts (coordination conjunctions, list/tally patterns, multiple independent claims), returning a
SIMPLE / BUNDLED verdict. It is the machine-testable core of the atomicity stage; the final decision
to split vs skip, and the routing of the unprocessable remainder, stays in SKILL.md (LADR-002).

Conservative on purpose: a candidate is only flagged BUNDLED on strong signals, so it never
over-splits a genuinely single fact.
"""

import argparse
import json
import re
import sys

# Strong conjunction signals that a single record likely asserts more than one independent fact.
_DISCOURSE = [
    r"\b(?:and|but|also|however|furthermore|moreover|additionally|whereas|while)\b",
    r"\b(?:because|since|so that|therefore|thus)\b",
    r"\b(?:which|that explains|also means)\b",
]
_TALLY = [
    # Leading negative lookbehind (not \b) so "local-first" or "v1-first" does not count "first"
    # as an enumerator — a common false positive in product phrasing.
    r"(?<![-\w])(?:first|second|third|fourth|fifth|finally|lastly)\b",
    r"(?:\b\d+\b|\b(?:two|three|four|five|several|many))\s+(?:things?|points?|reasons?|facts?)\b",
    r"\b(?:both|all of|each of)\b",
]
_SPLIT = [
    r"\b(?:this|that|the latter|the former)\b",
    r"\b(?:respectively|separately|independently)\b",
]

_COMPILED_DISCOURSE = [re.compile(p, re.IGNORECASE) for p in _DISCOURSE]
_COMPILED_TALLY = [re.compile(p, re.IGNORECASE) for p in _TALLY]
_COMPILED_SPLIT = [re.compile(p, re.IGNORECASE) for p in _SPLIT]

# Count occurrence-based signals. Conjunctions are counted by match, not just by which pattern group
# fired — "and ... also ... but" is three independent claim junctions.
TALLY_THRESHOLD = 1
DISCOURSE_THRESHOLD = 2


def _count_matches(patterns, text):
    total = 0
    for pattern in patterns:
        total += len(pattern.findall(text))
    return total


def classify(text):
    """Return {'verdict': 'simple'|'bundled', 'signals': ['tally'|'discourse'|'split-ref', ...]}.

    Conservatively flags only strong multi-fact signals; a lone "and" between attributes is not a
    bundle. This is a detector for the atomicity stage, not the decision.
    """
    text = text or ""
    tally = _count_matches(_COMPILED_TALLY, text)
    discourse = _count_matches(_COMPILED_DISCOURSE, text)
    split_ref = _count_matches(_COMPILED_SPLIT, text)

    signals = []
    if tally:
        signals.append("tally")
    if discourse:
        signals.append("discourse")
    if split_ref:
        signals.append("split-ref")

    # Bundled when there is an enumerator/tally, or when two or more conjunction junctions suggest
    # several independent claims. A single "and" between attributes is not a bundle.
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
        text = item.get("description", "") + " " + item.get("statement", "")
        verdict = classify(text)
        results.append({"candidate_index": index, **verdict})

    print(json.dumps({"results": results}, indent=2))


if __name__ == "__main__":
    main()
