# NFR-02 semantic evidence - 2026-09-17

> **Superseded in part on 2026-09-29 — one scenario was measuring behaviour the shipped write path
> cannot perform.** The 1.0000 / 1.0000 result below is kept as the record of what was measured on that
> date. It included a scenario (`s4`) that expected a **cross-group** same-subject match to produce a
> `version_bump` against a memory in another group. Under group-scoped identity — decided 2026-09-28,
> `(group, uuid)` — the version-target lookup refuses a foreign uuid, so that verdict is unachievable and
> the write would 404. Re-scoring this run against the corrected expectation gives **recall 0.9,
> precision 0.8333**: the earlier perfect score was partly earned by an impossible behaviour.
>
> The corrected run is `model-verdicts-2026-09-29.json` (1.0 / 1.0). This file is **not** superseded in
> full — nine of ten scenarios are unaffected — and the 09-17 verdicts file is deliberately left on disk
> rather than edited, because it is the record of that run, not a configuration.
>
> Root cause worth carrying: the fixture set, the committed verdicts and the NFR's own acceptance
> criteria all encoded cross-group matching, and the identity decision did not sweep the *evidence*
> even though it swept the spec. The CI-gated harness asserted recall and precision of exactly 1.0, so
> the defect was certified rather than caught.

## Method

Model received only blinded fixture output from:

```bash
python3 -B .agents/skills/mimisbrunnr-context-memory/tests/fixtures/score_fixtures.py --emit-model-input
```

It did not read `scenarios.json`, which contains scorer-only identifiers, expected verdicts and author
notes. The blinded emitter strips all three fields. One same-session normalization added the required
`not_product_fact` output field without revisiting scenarios. Final verdicts are committed as
`model-verdicts-2026-09-17.json`.

## Result

```bash
python3 -B .agents/skills/mimisbrunnr-context-memory/tests/fixtures/score_fixtures.py \
  --model-verdicts .agents/skills/mimisbrunnr-context-memory/tests/fixtures/model-verdicts-2026-09-17.json
```

- Semantic scenarios: 10
- Genuine-conflict adversarial scenario: passed
- Authority-resolvable controls: 2 passed
- Related-but-distinct negative control: passed
- Recall: 1.0000
- Precision: 1.0000

Model/provider identity was not exposed by delegated runner. This evidence validates one blinded
judgement run, not universal model quality. Deterministic plumbing remains separately CI-gated.
