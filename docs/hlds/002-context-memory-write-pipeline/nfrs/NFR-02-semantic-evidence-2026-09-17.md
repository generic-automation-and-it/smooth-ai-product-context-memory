# NFR-02 semantic evidence - 2026-09-17

## Method

Model received only blinded fixture output from:

```bash
python3 -B .agents/skills/mimisbrunnr-context-memory/tests/fixtures/score_fixtures.py --emit-model-input
```

It did not read `scenarios.json`, which contains scorer-only expected verdicts. First response selected
all ten core verdicts correctly but used unsupported output spellings. One retry supplied only wire
vocabulary constraints, then a same-session normalization renamed generic UUID fields to contract
fields without revisiting scenarios. Final verdicts are committed as
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
