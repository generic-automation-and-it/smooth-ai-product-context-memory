#!/usr/bin/env python3
"""On-demand score for the LLM-eval semantic/atomicity fixtures.

Non-circular: the EXPECTED verdicts are authored independently in scenarios.json; this script takes a
JSON array of the model's own verdicts and scores them against the expected set. The criterion is
countable — exact match on the enumerated verdict vocabulary — plus precision/recall over the fixed
recall set.

Verdicts are matched to scenarios by `id`, not by position. Positional matching meant that inserting,
deleting or reordering a scenario silently misaligned every verdict after it, and because
`run_tests.py` asserts the committed run scores exactly 1.0/1.0, the harness would then have
certified the wrong verdicts against the wrong scenarios with no test failing. The blinded input
therefore carries each scenario's `id` — an identifier is not an answer, so emitting it does not
unblind the run — and the model echoes it back.

A verdicts file with no ids at all is refused unless `--allow-legacy-positional` is passed. That flag
exists only so a superseded dated run stays re-scorable, which is how the 0.9 / 0.8333 figure was
obtained; a new run may not use it.

This is NOT CI-gated. Run on demand after a model has produced its verdicts:

    python3 tests/fixtures/score_fixtures.py --emit-model-input > model_input.json
    # Give only model_input.json to the model, then score its output:
    python3 tests/fixtures/score_fixtures.py --model-verdicts model_output.json

To re-score a dated run against the fixture it was actually taken against:

    python3 tests/fixtures/score_fixtures.py --fixtures tests/fixtures/scenarios-2026-09-17.json \
        --model-verdicts tests/fixtures/model-verdicts-2026-09-17.json --allow-legacy-positional
"""

import argparse
import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
FIXTURES = HERE / "scenarios.json"


def load_fixtures(path=None):
    with open(path or FIXTURES, "r", encoding="utf-8") as fh:
        return json.load(fh)["scenarios"]


# Expected-side fields compared by equality against the model verdict object when declared.
# "reason" is authored explanation text, not a criterion; "reason_must_be_nonempty" and
# "must_not_contain" carry their own assertion semantics below.
AUX_EQUALITY_FIELDS = ("target_uuid", "link_uuid", "relation", "count", "diverged", "not_product_fact", "authority")

# Which axis of NFR-02's two-axis measurement a scenario exercises. Declared on the fixture rather
# than inferred from the verdict word, because the acceptance criterion is about *pairs* — a positive
# pair exercising recall and a negative control exercising precision — and a scenario whose expected
# verdict happens to be new_memory for a reason unrelated to matching would silently inflate the
# negative count if it were inferred.
#   recall_positive    a same-subject pair that must collapse to a version bump
#   precision_negative a related-but-distinct pair that must NOT collapse
#   not_dedup          a scenario for another stage; carries neither axis
AXIS_VALUES = ("recall_positive", "precision_negative", "not_dedup")


def axis_balance(fixtures):
    positives = [f["id"] for f in fixtures if f.get("axis") == "recall_positive"]
    negatives = [f["id"] for f in fixtures if f.get("axis") == "precision_negative"]
    return positives, negatives


def scenario_matches(expected, got):
    """True only when the verdict word AND every declared auxiliary expectation hold."""
    got_dict = got if isinstance(got, dict) else {}
    got_verdict = got_dict.get("verdict") if isinstance(got, dict) else got
    if got_verdict != expected["verdict"]:
        return False
    for field in AUX_EQUALITY_FIELDS:
        if field in expected and got_dict.get(field) != expected[field]:
            return False
    if expected.get("reason_must_be_nonempty") and not got_dict.get("reason"):
        return False
    banned = expected.get("must_not_contain", [])
    if banned:
        rendered = json.dumps(got_dict) if isinstance(got, dict) else str(got)
        if any(token in rendered for token in banned):
            return False
    return True


def pair_by_id(fixtures, model, allow_legacy_positional):
    """Return [(fixture, verdict)] in fixture order, refusing anything that would pair by accident."""
    known = {fixture["id"]: fixture for fixture in fixtures}
    if len(known) != len(fixtures):
        raise SystemExit("score: the fixture contains a duplicate id")

    by_id = {}
    for entry in model:
        if not isinstance(entry, dict) or "id" not in entry:
            if allow_legacy_positional:
                return list(zip(fixtures, model)), True
            raise SystemExit(
                "score: a verdict carries no 'id', so it can only be paired by position. Pass "
                "--allow-legacy-positional to score a superseded dated run taken against an older "
                "fixture; a new run must echo each scenario's id."
            )
        if entry["id"] not in known:
            raise SystemExit(f"score: verdict names unknown scenario id {entry['id']!r}")
        if entry["id"] in by_id:
            raise SystemExit(f"score: scenario id {entry['id']!r} was judged more than once")
        by_id[entry["id"]] = entry

    missing = [fixture["id"] for fixture in fixtures if fixture["id"] not in by_id]
    if missing:
        raise SystemExit(f"score: no verdict for scenario(s) {', '.join(missing)}")
    return [(known[fixture["id"]], by_id[fixture["id"]]) for fixture in fixtures], False


def main():
    parser = argparse.ArgumentParser(prog="score_fixtures")
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--model-verdicts",
                      help="JSON array of the model's verdicts, each echoing its scenario id")
    mode.add_argument("--emit-model-input", action="store_true",
                      help="print scenarios without expected verdicts for a blinded model run")
    parser.add_argument("--allow-legacy-positional", action="store_true",
                        help="pair by position for a dated run that predates id echoing (re-scoring only)")
    parser.add_argument("--fixtures",
                        help="score against this fixture file instead of the live one; required to "
                             "re-score a dated run the live fixture has since outgrown")
    args = parser.parse_args()

    fixtures = load_fixtures(args.fixtures)
    if args.emit_model_input:
        # `id` is emitted: it identifies the row without revealing the answer, and without it the
        # scorer can only pair by position. `expected`, `note` and `axis` stay withheld — `axis`
        # would tell the model which way the pair is meant to fall.
        blinded = [{key: value for key, value in fixture.items()
                    if key not in ("expected", "note", "axis")}
                   for fixture in fixtures]
        print(json.dumps({"scenarios": blinded}, indent=2))
        return

    with open(args.model_verdicts, "r", encoding="utf-8") as fh:
        model = json.load(fh)

    pairs, positional = pair_by_id(fixtures, model, args.allow_legacy_positional)
    if len(model) != len(fixtures):
        print(f"score: expected {len(fixtures)} verdicts, got {len(model)}", file=sys.stderr)
        sys.exit(1)

    positives, negatives = axis_balance(fixtures)
    live = not args.fixtures and not args.allow_legacy_positional
    if live and len(negatives) < len(positives):
        print(f"score: {len(negatives)} negative controls against {len(positives)} positive pairs. "
              f"NFR-02 requires at least as many negatives as positives: a matcher that matches "
              f"nothing scores perfect precision and fills the store with duplicates.", file=sys.stderr)
        sys.exit(1)

    correct = 0
    rows = []
    for fixture, got in pairs:
        expected = fixture["expected"]
        got_verdict = got.get("verdict") if isinstance(got, dict) else got
        ok = scenario_matches(expected, got)
        correct += 1 if ok else 0
        rows.append(
            {
                "id": fixture["id"],
                "axis": fixture.get("axis", "unlabelled"),
                "expected": expected["verdict"],
                "got": got_verdict,
                "match": ok,
                "reason_present": bool((got if isinstance(got, dict) else {}).get("reason")),
            }
        )

    total = len(fixtures)
    recall = correct / total if total else 0.0
    # Precision over the dedup-class scenarios: for each, the model must not over-merge (collapse a
    # distinct claim into a version bump). Counted as the share of dedup/divergence scenarios whose
    # verdict did not wrongly claim a version_bump/merge.
    dedup_scenarios = [f for f in fixtures if f["stage"] in ("dedup", "divergence")]
    over_merge = 0
    for fixture, got in pairs:
        if fixture["stage"] not in ("dedup", "divergence"):
            continue
        expected = fixture["expected"]["verdict"]
        got_verdict = got.get("verdict") if isinstance(got, dict) else got
        if expected in ("new_memory", "genuine_conflict") and got_verdict in ("version_bump", "merge"):
            over_merge += 1
    precision = 1.0 - (over_merge / len(dedup_scenarios)) if dedup_scenarios else 1.0

    print(json.dumps({
        "rows": rows,
        "recall": round(recall, 4),
        "precision": round(precision, 4),
        "counts": {
            "scenarios": total,
            "correct": correct,
            "over_merge": over_merge,
            "positive_pairs": len(positives),
            "negative_controls": len(negatives),
        },
        "paired_by": "position (legacy)" if positional else "id",
    }, indent=2))
    if correct != total or over_merge:
        sys.exit(1)


if __name__ == "__main__":
    main()
