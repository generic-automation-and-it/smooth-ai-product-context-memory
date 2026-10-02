# NFR-02 semantic evidence - 2026-09-17

> **Superseded in part on 2026-09-29 — one scenario was measuring behaviour the shipped write path
> cannot perform.** The 1.0000 / 1.0000 result below is kept as the record of what was measured on that
> date. It included a scenario (`s4`) that expected a **cross-group** same-subject match to produce a
> `version_bump` against a memory in another group. Under group-scoped identity — decided 2026-09-28,
> `(group, uuid)` — the version-target lookup refuses a foreign uuid, so that verdict is unachievable and
> the write would 404. Re-scoring this run against the corrected expectation gives **accuracy 0.9,
> precision 0.5**: the earlier perfect score was partly earned by an impossible behaviour. That pair is
> itself a correction, dated 2026-09-30 — the figures originally quoted here, `recall 0.9 / precision
> 0.8333`, were **accuracy** and a precision whose denominator included four scenarios that cannot
> over-merge. This run has **no recall figure at all**: the frozen ten-scenario fixture predates the
> `axis` labels and declares no positive pair, so recall has no denominator for it, and the scorer now
> prints `null` rather than a number that would read as a measurement.
>
> **Superseded in full on 2026-09-29 by the balanced, same-group re-measurement**
> ([NFR-02-semantic-evidence-2026-09-29-balanced.md](./NFR-02-semantic-evidence-2026-09-29-balanced.md)),
> which additionally fixes the unbalanced negative controls this run also suffered from. The
> intermediate correction is `model-verdicts-2026-09-29.json` (1.0 / 1.0).
>
> **The figures above are reproducible on demand, not quoted.** This run's fixture is frozen
> as `scenarios-2026-09-29.json` in the skill's fixtures directory, so re-scoring needs
> `--fixtures scenarios-2026-09-29.json --allow-legacy-positional` — without the frozen fixture the
> live set has grown and the comparison would be a length error rather than a measurement. The verdicts
> file is deliberately left on disk unedited, because it is the record of that run, not a configuration.
>
> Root cause worth carrying: the fixture set, the committed verdicts and the NFR's own acceptance
> criteria all encoded cross-group matching, and the identity decision did not sweep the *evidence*
> even though it swept the spec. The CI-gated harness asserted recall and precision of exactly 1.0, so
> the defect was certified rather than caught. A second, independent instance of the same shape is
> recorded in the 2026-09-30 row of the balanced document: the *instrument* also needed correcting, and
> it was wrong in a way that flattered a matcher which collapsed nothing.

## Method

Model received only blinded fixture output from:

```bash
python3 -B .agents/skills/mimisbrunnr-odin-context-memory/tests/fixtures/score_fixtures.py --emit-model-input
```

It did not read `scenarios.json`, which contains scorer-only identifiers, expected verdicts and author
notes. The blinded emitter strips all three fields. One same-session normalization added the required
`not_product_fact` output field without revisiting scenarios. Final verdicts are committed as
`model-verdicts-2026-09-17.json`.

## Result

```bash
cd .agents/skills/mimisbrunnr-odin-context-memory/tests/fixtures
python3 score_fixtures.py \
  --fixtures scenarios-2026-09-29.json \
  --model-verdicts model-verdicts-2026-09-17.json \
  --allow-legacy-positional
```

- Semantic scenarios: 10
- Genuine-conflict adversarial scenario: passed
- Authority-resolvable controls: 2 passed
- Related-but-distinct negative control: passed
- Recall: 1.0000
- Precision: 1.0000

Model/provider identity was not exposed by delegated runner. This evidence validates one blinded
judgement run, not universal model quality. Deterministic plumbing remains separately CI-gated.
