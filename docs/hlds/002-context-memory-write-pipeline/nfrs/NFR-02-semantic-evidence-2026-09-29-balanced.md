# NFR-02 semantic evidence — 2026-09-29 (balanced, same-group)

The re-measurement the 2026-09-28 withdrawal called for. The claim is unchanged — *semantic subject
matching is accurate on both axes* — but the evidence underneath it is new, and this run is the first
whose fixture the shipped write path can actually produce.

**The scorer measured accuracy and printed it under the name recall.** `recall = correct / total` over
all fourteen scenarios, so a matcher that collapsed *nothing* scored 0.857, and precision's
denominator was every dedup-class scenario whether or not the model claimed anything on it — five of
them cannot over-merge, so each one flattered the score. Both figures are corrected below, and the
`0.9 / 0.8333` pair quoted for the 2026-09-17 run was itself **accuracy** and a **flattering
precision**: corrected, that run is `accuracy 0.9 / precision 0.5`, and it has no recall figure at all
because the frozen fixture predates `axis` and declares no positive pair. The 2026-09-29-balanced run is
unaffected: it is 1.0000 on all three.

## What changed, and why the old evidence could not simply be re-run

**The cross-group scenario.** `s4` expected a same-subject match in another group to produce a
`version_bump` against a foreign uuid. Group-scoped identity (`(group, uuid)`, decided 2026-09-28)
refuses that target, so the verdict was unachievable and the write would have 404'd. The scenario is
retained as a **negative control** rather than deleted: a cross-group match is a new memory plus a
typed link, so "must not bump" is a real precision case.

**The controls were unbalanced.** The acceptance criteria require negative controls at least as
numerous as positive pairs — a matcher that matches nothing scores perfect precision and fills the
store with duplicates, which is the failure this NFR exists to catch. Every scenario now carries an
explicit `axis` label (`recall_positive`, `precision_negative`, `not_dedup`) rather than the balance
being inferred from the expected verdict word, because a scenario expecting `new_memory` for a reason
unrelated to matching would otherwise inflate the negative count. The scorer and the harness both
refuse an unbalanced set.

| | Count | Scenarios |
|---|---|---|
| Recall positives | 2 | `s2`, `s14` |
| Precision negatives | 5 | `s4`, `s8`, `s11`, `s12`, `s13` |
| Other stages | 7 | redact, atomicity, links, scope, divergence, authority ×2 |

The three new negatives are the shapes a matcher most often over-merges: same domain and vocabulary
with a different claim (`s11`, NFR-02's own worked example), a subject whose only difference is a
contradicted value (`s12`), and a claim one path segment apart from its twin (`s13`). The second recall
positive (`s14`) exists so recall does not rest on a single scenario.

**The scorer paired verdicts to scenarios by position.** Inserting, deleting or reordering a scenario
silently misaligned every verdict after it — and because the harness asserts the committed run scores
exactly 1.0 / 1.0, the harness would have *certified* the wrong verdicts against the wrong scenarios
with no test failing. Verdicts are now paired by `id`; the blinded emitter emits `id` and the scorer
refuses a verdicts file with no ids unless `--allow-legacy-positional` is passed explicitly. What keeps
a run blinded is the emitter withholding `expected`, `note` and `axis`, which the harness asserts; the
`id` is emitted for pairing. The earlier justification for emitting it — that an identifier is not an
answer — was not true of this fixture set, where two ids name their own expected verdicts. That is a
transparency property of a *committed* evidence file, recorded rather than papered over: no id
scrubbing was added to the blinded input, which was already sound.

**The balance refusal was reachable past.** `--fixtures` switched it off along with the positional
pairing, so any frozen fixture could be scored with no negative controls at all — the exact condition
NFR-02 exists to prevent, one extra flag away. Balance is a property of the fixture rather than of how
the run was taken, so it is now checked for every run that is not a re-score of a superseded dated one,
and a fixture declaring no positive pair is refused as unmeasurable rather than scored.

## Method

The model received only the blinded emitter output:

```bash
python3 -B .agents/skills/mimisbrunnr-odin-context-memory/tests/fixtures/score_fixtures.py --emit-model-input
```

It did not read `scenarios.json`, which holds the expected verdicts, author notes and the `axis` label.
The emitter withholds all three. Verdicts are committed as
`model-verdicts-2026-09-29-balanced.json`, each echoing its scenario `id`.

The judging model was **Space Bunny Free** (the session's own model), not a separately commissioned
run. That is a real difference from the delegated-runner setup the earlier evidence used, and it is
recorded rather than glossed: this is one blinded judgement by one model, which validates that the
*fixture and scorer* now measure what they claim — it is not a claim about model quality in general.

## Result

```bash
python3 -B .agents/skills/mimisbrunnr-odin-context-memory/tests/fixtures/score_fixtures.py \
  --model-verdicts .agents/skills/mimisbrunnr-odin-context-memory/tests/fixtures/model-verdicts-2026-09-29-balanced.json
```

| | |
|---|---|
| Scenarios | 14 |
| Correct | 14 |
| Recall (over the 2 declared positive pairs) | 1.0000 |
| Precision (over the 2 collapses actually claimed) | 1.0000 |
| Accuracy (over all 14) | 1.0000 |
| Over-merge events | 0 |
| Positive pairs / negative controls | 2 / 5 |
| Paired by | `id` |

Counts rather than a boolean, per the acceptance criteria: a regression on either axis shows up as a
changed `correct` count or a non-zero `over_merge`, not as a pass/fail flag.

**Three denominators, and why the separation is the measurement.** Recall counts only the scenarios
declared `recall_positive`, so a scenario for another stage and a negative control the model got right
cannot lift it — a matcher that collapsed nothing scores 0.0000. Precision counts only the collapses
the model actually claimed, so a claim that was not a collapse costs and a scenario it said nothing
about does not flatter it. Accuracy is the whole-scenario figure, reported under that name because it
is what the old `recall` was: `correct / total`. Collapsing all ten dedup-class scenarios scores
`recall 1.0 / precision 0.2 / accuracy 0.4286` — it does collapse both positive pairs, so recall is
the *right* answer for it and precision is what catches it, which is why recall alone cannot be the
pass criterion. The 0.857 that the old formula printed belonged to the opposite matcher: one that
collapses nothing and gets the other twelve scenarios right, where `correct / total` reported accuracy
under recall's name.

## The superseded runs stay re-scorable

A dated verdicts file is a record of what a model said on a day, and it is only re-scorable against the
fixture that run actually saw. `scenarios-2026-09-29.json` freezes the ten-scenario set, so both
earlier runs can be re-scored exactly:

```bash
cd .agents/skills/mimisbrunnr-odin-context-memory/tests/fixtures
for run in 2026-09-17 2026-09-29; do
  python3 score_fixtures.py --fixtures scenarios-2026-09-29.json \
    --model-verdicts model-verdicts-$run.json --allow-legacy-positional
done
```

| Run | Recall | Precision | Accuracy | Mismatch |
|---|---|---|---|---|
| `model-verdicts-2026-09-17.json` | not measured | 0.5000 | 0.9000 | `s4` — the impossible cross-group bump |
| `model-verdicts-2026-09-29.json` | not measured | 1.0000 | 1.0000 | none |

**Recall reads "not measured", not 0.0000, and the table shows that as `null`.** The frozen
ten-scenario fixture predates `axis`, so it declares no positive pair and cannot produce a recall
denominator at all. Printing 0.0 there would invite "the model had zero recall" where the truth is
"this run was never a recall measurement" — the same class of error as the one being corrected, in the
opposite direction. Only reachable via `--allow-legacy-positional`, since a current run is refused for
the same condition.

**The `0.9 / 0.8333` pair this document used to quote was wrong, and is corrected here rather than
left to be re-derived.** Its two figures were accuracy and a precision whose denominator included four
scenarios that cannot over-merge. The corrected figures are `accuracy 0.9000 / precision 0.5000` for
the 2026-09-17 run: it made two collapse claims, one of which was the impossible cross-group bump. The
pair is still reproducible on demand, which is what let the withdrawn-evidence correction be checked
rather than believed — it is simply reproducible at different values than the ones previously quoted.

**The frozen fixture is named for the day it was frozen, not the day the first run was taken.** It was
committed on 2026-09-29 and its `s4` already carries the corrected expectation (`new_memory`, after the
group-scoped identity decision). It was previously named `scenarios-2026-09-17.json`, which asserted a
provenance it does not have: **the fixture the 2026-09-17 run actually saw was never committed**, so the
withdrawn 1.0 / 1.0 is not reconstructible from any artefact in this repository, and an auditor
following the old name would conclude the opposite — that it never happened. The rename is the honest
record. The figures in the table above are not a re-derivation of that run either: they are the
2026-09-17 run's verdicts re-scored against the *corrected* expectation, where `s4` is the sole
mismatch. Both facts are load-bearing, and conflating them is how a withdrawn measurement gets cited
as though it were reproduced.

**This run's own verdicts are deliberately not in that table.** `model-verdicts-2026-09-29-balanced.json`
echoes `s11`–`s14`, which the ten-scenario freeze does not contain, so adding it to the loop above would
abort on `score: verdict names unknown scenario id` instead of printing a figure — the same rule the
paragraph above states, applied to this document's own table. It re-scores against the live
fourteen-scenario `scenarios.json` using the command in **Result** above (1.0000 / 1.0000 / 1.0000),
and only for as long as that live fixture stays as committed.

## Boundaries

- One model, one run, fourteen scenarios. A perfect score on a fixture this size is evidence that the
  judgement is not obviously broken on these shapes, not a bound on its accuracy.
- **Two of the fourteen are not an independent measurement at all.** The fixture is authored
  independently of the judging run, which is the property that matters, but it was still written in
  this repository by the same author as the scenarios the model was asked to reason about. Scenarios
  authored by a party with no stake in the result would be a stronger fixture, and nothing here
  evidences that.
- **The scoring machinery changed after the run was taken.** The 1.0 / 1.0 / 1.0 above is that run
  re-scored under the corrected denominators, not a fresh run against the corrected scorer. The two
  facts are separable and were kept so: the verdicts file is the record of one model's judgements, the
  scorer is the instrument, and correcting an instrument does not require re-asking the model — but it
  also does not re-test it. A run taken *after* the correction is a stronger claim than this one.
- The judgement is the model's, not a re-derivation of the rules. Encoding the rule in the fixture and
  observing it hold would measure nothing, which is why the fixture is authored independently.
- Deterministic plumbing — payload caps, credential selection, transport guards, the redaction gate —
  remains CI-gated separately and is not evidenced here.
- `s12` is scored `new_memory` on the *matching* axis: a contradicted value is a disagreement to
  surface, not a restatement to collapse, and surfacing it is the divergence stage's job. The fixture
  asserts only that matching does not merge it.
