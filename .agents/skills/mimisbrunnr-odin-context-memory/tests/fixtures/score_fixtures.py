#!/usr/bin/env python3
"""On-demand score for the LLM-eval semantic/atomicity fixtures.

Non-circular: the EXPECTED verdicts are authored independently in scenarios.json; this script takes a
JSON array of the model's own verdicts and scores them against the expected set. The criterion is
countable — exact match on the enumerated verdict vocabulary — plus precision/recall over the fixed
recall set.

Three numbers, and the difference between them is the whole point of this file:

  recall    correct / the scenarios declared `recall_positive`. The share of genuine restatements the
            model collapsed instead of creating a second memory. A matcher that matches nothing scores
            0.0 here, which is the only way this number can mean anything.
  precision correct / the predictions the model actually made. The share of claimed collapses that were
            really collapses into the expected memory, over the dedup-class scenarios it claimed them
            on. A collapse into the wrong target counts against it (`wrong_target`).
  accuracy  correct / every scenario. Reported because the recall used to be this number under the
            name recall, which let a run that missed a scenario outright still print a healthy score.

Verdicts are matched to scenarios by `id`, not by position. Positional matching meant that inserting,
deleting or reordering a scenario silently misaligned every verdict after it, and because
`run_tests.py` asserts the committed run scores exactly 1.0/1.0, the harness would then have
certified the wrong verdicts against the wrong scenarios with no test failing. The blinded input
therefore carries each scenario's `id` and the model echoes it back.

What keeps the run blinded is the emitter withholding `expected`, `note` and `axis` — asserted by
`run_tests.py::SemanticFixtureTests`. The `id` is emitted for pairing, and an id is not a general
licence to read intent off a string: in this fixture set `s4-cross-group-match-is-not-a-bump` and
`s8-near-miss-negative` name their own expected verdicts, so anyone reading the repository can see
the answers. That is a transparency property of a committed evidence file, not a property of the
blinded input, and it is recorded here rather than papered over with a claim about identifiers.

A verdicts file with no ids at all is refused unless `--allow-legacy-positional` is passed. That flag
exists only so a superseded dated run stays re-scorable, which is how the 0.9 / 0.8333 figure was
obtained; a new run may not use it.

This is NOT CI-gated. Run on demand after a model has produced its verdicts:

    python3 tests/fixtures/score_fixtures.py --emit-model-input > model_input.json
    # Give only model_input.json to the model, then score its output:
    python3 tests/fixtures/score_fixtures.py --model-verdicts model_output.json

To re-score a dated run against the fixture it was actually taken against:

    python3 tests/fixtures/score_fixtures.py --fixtures tests/fixtures/scenarios-2026-09-29.json \
        --model-verdicts tests/fixtures/model-verdicts-2026-09-17.json --allow-legacy-positional
    python3 tests/fixtures/score_fixtures.py --fixtures tests/fixtures/scenarios-2026-09-29-balanced.json \
        --model-verdicts tests/fixtures/model-verdicts-2026-09-29-balanced.json
"""

import argparse
import json
import re
import sys
import unicodedata
from pathlib import Path

HERE = Path(__file__).resolve().parent
FIXTURES = HERE / "scenarios.json"


def load_fixtures(path=None):
    with open(path or FIXTURES, "r", encoding="utf-8") as fh:
        return json.load(fh)["scenarios"]


# Expected-side fields compared by equality against the model verdict object when declared.
# "reason" is authored explanation text, not a criterion; "reason_must_be_nonempty", "redacted_must_keep",
# "redacted_must_cover" and "must_not_contain" carry their own assertion semantics below.
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

# Stages on which a collapse verdict is a meaningful answer, and the verdict words that mean one.
# Declared here rather than inlined so precision's denominator is a stated question — "of the collapses
# claimed, how many were real?" — instead of an accident of which stages happened to be enumerated.
DEDUP_STAGES = ("dedup", "divergence")
MERGE_VERDICTS = ("version_bump", "merge")

VERDICT_SHAPE = {
    "every_stage": "an object with the scenario's `id`, a `verdict` word and a non-empty `reason`",
    "redact": "also a `redacted` object mapping every candidate_* text field to its scrubbed text",
}


def axis_balance(fixtures):
    positives = [f["id"] for f in fixtures if f.get("axis") == "recall_positive"]
    negatives = [f["id"] for f in fixtures if f.get("axis") == "precision_negative"]
    return positives, negatives


def normalised(text):
    """Case-, whitespace- and invisible-character-insensitive form, so `akia…` or a key split by a
    space, a line break or a zero-width character still counts as the token."""
    text = "".join(ch for ch in text if unicodedata.category(ch) != "Cf")
    return re.sub(r"\s+", "", text).casefold()


def rendered_text(value):
    """Every string a verdict carries, keys included, joined in order.

    Read from the decoded strings rather than from `json.dumps`, which writes a line break or a tab as
    the two characters `\\n`/`\\t` — not whitespace, so `normalised` left them in place and a key split
    across lines passed the banned-token check (issue 184)."""
    if isinstance(value, dict):
        return "".join(str(key) + rendered_text(item) for key, item in value.items())
    if isinstance(value, (list, tuple)):
        return "".join(rendered_text(item) for item in value)
    return "" if value is None else str(value)


def has_reason(got):
    """A non-blank string `reason`: the shape every verdict is told to carry (`VERDICT_SHAPE`)."""
    reason = got.get("reason") if isinstance(got, dict) else None
    return isinstance(reason, str) and bool(reason.strip())


def scenario_matches(expected, got):
    """True only when the verdict word AND every declared auxiliary expectation hold."""
    got_dict = got if isinstance(got, dict) else {}
    got_verdict = got_dict.get("verdict") if isinstance(got, dict) else got
    if got_verdict != expected["verdict"]:
        return False
    for field in AUX_EQUALITY_FIELDS:
        if field in expected and got_dict.get(field) != expected[field]:
            return False
    # Every stage, not only the scenarios declaring `reason_must_be_nonempty`: the blinded input
    # tells the model every verdict carries a non-empty reason, and a reasonless verdict scored as
    # correct on the thirteen scenarios that did not repeat the rule (issue 184). Every committed run
    # carries a reason on every verdict, so no recorded figure moves.
    if not has_reason(got):
        return False
    # A `scrub` verdict is a claim about content, so the scrubbed content has to be there to be
    # checked. Without this a bare `scrub` — or a redaction that only lowercased the key — scored as
    # correct, because the banned-token check only ever saw what the verdict happened to echo
    # (issue 182).
    covered = expected.get("redacted_must_cover", [])
    if covered:
        redacted = got_dict.get("redacted")
        if not isinstance(redacted, dict):
            return False
        keep = expected.get("redacted_must_keep", {})
        for field in covered:
            text = redacted.get(field)
            if not isinstance(text, str) or not text.strip():
                return False
            # Scrubbing is replacing the secret, not dropping the claim around it: a field reduced to
            # `<redacted>` passed `must_not_contain` as easily as a correct scrub did (issue 186).
            if any(normalised(token) not in normalised(text) for token in keep.get(field, [])):
                return False
    banned = expected.get("must_not_contain", [])
    if banned:
        rendered = normalised(rendered_text(got))
        if any(normalised(token) in rendered for token in banned):
            return False
    return True


def pair_by_id(fixtures, model, allow_legacy_positional):
    """Return [(fixture, verdict)] in fixture order, refusing anything that would pair by accident."""
    known = {fixture["id"]: fixture for fixture in fixtures}
    if len(known) != len(fixtures):
        raise SystemExit("score: the fixture contains a duplicate id")

    identified = [isinstance(entry, dict) and "id" in entry for entry in model]
    if allow_legacy_positional and not any(identified):
        return list(zip(fixtures, model)), True
    by_id = {}
    for entry in model:
        if not isinstance(entry, dict) or "id" not in entry:
            if any(identified):
                # Some verdicts name their scenario and some do not: a run that is neither by-id nor
                # positional. Falling back to position here let an id-bearing verdict be scored against
                # whatever scenario sat at its position (issue 186), so a mixed run is always refused.
                raise SystemExit("score: some verdicts carry an 'id' and some do not; a mixed run "
                                 "cannot be paired, by id or by position")
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
        # Without the writing group a model cannot tell a same-group bump from a cross-group twin,
        # and that distinction is what several dedup scenarios score. Refused at emission only, so a
        # frozen fixture that predates the field stays re-scorable.
        ungrouped = [fixture["id"] for fixture in fixtures
                     if fixture.get("recall_set") and not fixture.get("candidate_group_uuid")]
        if ungrouped:
            raise SystemExit("score: scenario(s) with a recall set but no candidate_group_uuid: "
                             + ", ".join(ungrouped))
        # `id` is emitted: it identifies the row without revealing the answer, and without it the
        # scorer can only pair by position. `expected`, `note` and `axis` stay withheld — `axis`
        # would tell the model which way the pair is meant to fall.
        blinded = [{key: value for key, value in fixture.items()
                    if key not in ("expected", "note", "axis")}
                   for fixture in fixtures]
        # Output shape, not an answer: a redact-stage verdict is scored on the scrubbed text, so the
        # model has to be told to return it.
        print(json.dumps({"verdict_shape": VERDICT_SHAPE, "scenarios": blinded}, indent=2))
        return

    with open(args.model_verdicts, "r", encoding="utf-8") as fh:
        model = json.load(fh)

    pairs, positional = pair_by_id(fixtures, model, args.allow_legacy_positional)
    if len(model) != len(fixtures):
        print(f"score: expected {len(fixtures)} verdicts, got {len(model)}", file=sys.stderr)
        sys.exit(1)

    # Balance is a property of the fixture, so it is checked for every run that is not a re-score of
    # a superseded dated one. `--fixtures` used to switch the check off along with the positional
    # pairing, which meant any frozen fixture could be scored with no negatives at all — the exact
    # condition NFR-02 exists to prevent, reachable by passing one extra flag.
    positives, negatives = axis_balance(fixtures)
    # Keyed on the pairing result, not the flag: a run that echoed ids and paired by id is not a
    # superseded re-score, so a fresh run scored against a frozen pre-`axis` fixture is still refused
    # rather than reported with no negative controls at all.
    if not positional:
        if not positives:
            print("score: no scenario declares axis=recall_positive, so recall has no denominator. "
                  "A fixture with no positive pair cannot measure recall.", file=sys.stderr)
            sys.exit(1)
        if len(negatives) < len(positives):
            print(f"score: {len(negatives)} negative controls against {len(positives)} positive pairs. "
                  f"NFR-02 requires at least as many negatives as positives: a matcher that matches "
                  f"nothing scores perfect precision and fills the store with duplicates.", file=sys.stderr)
            sys.exit(1)

    positive_ids = set(positives)
    correct = 0
    correct_positives = 0
    rows = []
    for fixture, got in pairs:
        expected = fixture["expected"]
        got_verdict = got.get("verdict") if isinstance(got, dict) else got
        ok = scenario_matches(expected, got)
        correct += 1 if ok else 0
        if ok and fixture["id"] in positive_ids:
            correct_positives += 1
        rows.append(
            {
                "id": fixture["id"],
                "axis": fixture.get("axis", "unlabelled"),
                "expected": expected["verdict"],
                "got": got_verdict,
                "match": ok,
                "reason_present": has_reason(got),
            }
        )

    total = len(fixtures)
    accuracy = correct / total if total else 0.0

    # Recall over the declared positive pairs only. The denominator is the scenarios whose expected
    # verdict is a collapse, so a scenario for another stage — or a negative control the model got
    # right — cannot lift it. This was `correct / total`, i.e. accuracy, wearing recall's name: a
    # matcher that collapsed nothing scored 0.857 here, and a reader took that for recall.
    # null, not 0.0, when the fixture declares no positive pair. A frozen fixture taken before
    # `axis` existed cannot measure recall at all, and printing 0.0 for it invites "the model had
    # zero recall" where the truth is "this run was never a recall measurement". Only reachable via
    # --allow-legacy-positional, since a current run is refused for the same condition.
    recall = correct_positives / len(positives) if positives else None

    # Precision over the collapses the model actually predicted. A false positive is a claimed
    # collapse that was not one, so the denominator is the predicted positives rather than every
    # dedup-class scenario: the previous form divided by all seven dedup scenarios, five of which
    # can never over-merge, so each one flattered the score.
    predicted_positives = 0
    correct_predictions = 0
    over_merge = 0
    wrong_target = 0
    for fixture, got in pairs:
        if fixture["stage"] not in DEDUP_STAGES:
            continue
        expected_verdict = fixture["expected"]["verdict"]
        got_verdict = got.get("verdict") if isinstance(got, dict) else got
        claimed = got_verdict in MERGE_VERDICTS
        should_claim = expected_verdict in MERGE_VERDICTS
        if claimed:
            predicted_positives += 1
            # A collapse into the wrong memory is not a correct collapse: it overwrites a claim the
            # candidate never restated. So the full match, target included, is what counts.
            if should_claim and scenario_matches(fixture["expected"], got):
                correct_predictions += 1
            elif should_claim:
                wrong_target += 1
            else:
                over_merge += 1
    precision = correct_predictions / predicted_positives if predicted_positives else 1.0

    print(json.dumps({
        "rows": rows,
        "recall": None if recall is None else round(recall, 4),
        "precision": round(precision, 4),
        "accuracy": round(accuracy, 4),
        "counts": {
            "scenarios": total,
            "correct": correct,
            "positive_pairs": len(positives),
            "positive_pairs_correct": correct_positives,
            "negative_controls": len(negatives),
            "predicted_positives": predicted_positives,
            "correct_predictions": correct_predictions,
            "over_merge": over_merge,
            "wrong_target": wrong_target,
        },
        "paired_by": "position (legacy)" if positional else "id",
    }, indent=2))
    if correct != total or over_merge:
        sys.exit(1)


if __name__ == "__main__":
    main()
