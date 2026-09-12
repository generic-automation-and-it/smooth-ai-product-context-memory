#!/usr/bin/env python3
"""On-demand score for the LLM-eval semantic/atomicity fixtures.

Non-circular: the EXPECTED verdicts are authored independently in scenarios.json; this script takes a
JSON array of the model's own verdicts (one per scenario, in order) and scores them against the
expected set. The criterion is countable — exact match on the enumerated verdict vocabulary — plus
precision/recall over the fixed recall set.

This is NOT CI-gated. Run on demand after a model has produced its verdicts:

    python3 tests/fixtures/score_fixtures.py --model-verdicts model_output.json
"""

import argparse
import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
FIXTURES = HERE / "scenarios.json"


def load_fixtures():
    with open(FIXTURES, "r", encoding="utf-8") as fh:
        return json.load(fh)["scenarios"]


def main():
    parser = argparse.ArgumentParser(prog="score_fixtures")
    parser.add_argument("--model-verdicts", required=True, help="JSON array of the model's verdicts, in scenario order")
    args = parser.parse_args()

    with open(args.model_verdicts, "r", encoding="utf-8") as fh:
        model = json.load(fh)

    fixtures = load_fixtures()
    if len(model) != len(fixtures):
        print(f"score: expected {len(fixtures)} verdicts, got {len(model)}", file=sys.stderr)
        sys.exit(1)

    correct = 0
    rows = []
    for fixture, got in zip(fixtures, model):
        expected = fixture["expected"]["verdict"]
        got_verdict = got.get("verdict") if isinstance(got, dict) else got
        ok = got_verdict == expected
        correct += 1 if ok else 0
        rows.append(
            {
                "id": fixture["id"],
                "expected": expected,
                "got": got_verdict,
                "match": ok,
                "reason_present": bool((got if isinstance(got, dict) else {}).get("reason")),
            }
        )

    total = len(fixtures)
    recall = correct / total if total else 0.0
    # Precision over the dedup-class scenarios: for each, the model must not over-merge (collapse a
    # distinct claim into a version bump). Counted as the share of non-negative-control scenarios
    # whose verdict did not wrongly claim a version_bump/merge.
    dedup_scenarios = [f for f in fixtures if f["stage"] in ("dedup", "divergence")]
    over_merge = 0
    for fixture, got in zip(fixtures, model):
        if fixture["stage"] not in ("dedup", "divergence"):
            continue
        expected = fixture["expected"]["verdict"]
        got_verdict = got.get("verdict") if isinstance(got, dict) else got
        if expected in ("new_memory", "leave_both") and got_verdict in ("version_bump", "merge"):
            over_merge += 1
    precision = 1.0 - (over_merge / len(dedup_scenarios)) if dedup_scenarios else 1.0

    print(json.dumps({"rows": rows, "recall": round(recall, 4), "precision": round(precision, 4)}, indent=2))
    if correct != total or over_merge:
        sys.exit(1)


if __name__ == "__main__":
    main()
