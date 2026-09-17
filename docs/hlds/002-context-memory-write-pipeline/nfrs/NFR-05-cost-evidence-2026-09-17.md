# NFR-05 cost evidence — 2026-09-17

## Method

Run from repository root:

```bash
python3 -B .agents/skills/mimisbrunnr-context-memory/tests/measure_cost.py
python3 -B .agents/skills/mimisbrunnr-context-memory/tests/run_tests.py
```

The deterministic fixture models one checkpoint containing ten atomic facts and a saturated baseline
recall of 200 synthetic cheap rows. It estimates contract bounds and serialized main-context bytes; it
does not execute live agent or HTTP workflows.
It never reads a real memory or emits memory content. Provider invocation and token counts are not
available from repository execution and are not inferred from logical fact counts.

## Structural Estimates

| Workflow | Agent invocations | Logical summary judgements | Logical keyword judgements | HTTP calls / recall passes | Blob I/O | Main-context bytes |
|---|---:|---:|---:|---|---|---:|
| Normal ten-fact write | 1 write agent | 10 | 10 | minimum 3: preflight, baseline query, set | 0–10 writes | digest only; provider measurement unavailable |
| Ten-fact dry-run | 1 write agent | 10 | 10 | same minimum 3 | 0 | digest only; provider measurement unavailable |
| Explicit deep search | 1 delegated agent | workflow-dependent | workflow-dependent | 1 baseline + at most 4 keyword + 5 traversal passes | 0 unless later selected drill-down | bounded conclusion only |
| Lookup | 1 read agent | n/a | n/a | bounded read | selected drill-down only | 3,030 synthetic bytes |
| Grounding | 1 read agent | n/a | n/a | bounded read | selected drill-down only | 14,784 synthetic bytes |
| Previous raw-row path | main session | n/a | n/a | baseline query | none by default | 107,401 synthetic bytes |

Deep-search tests cap candidate judgement at 400 unique UUID/version pairs. Default execution performs zero
optional deep-search passes. Dry-run and write use identical semantic stages; only `set` persistence and
blob writes differ. Delegation reduces representative main-context payload by 97.2% for lookup and
86.2% for grounding versus the 200-row synthetic baseline. Aggregate token spend may increase because
delegated agents establish their own context.

## Unavailable Measurements And Blocker

- Provider invocation count is platform-dependent and unavailable to this repository harness.
- Input, output and cache token counts are unavailable without provider usage telemetry.
- Logical judgement counts do not imply provider invocation counts; one invocation may batch many facts.
- Real end-to-end agent token telemetry and one live delegated capture/recall remain the named blocker
  for accepting NFR-05. Status therefore remains Draft.

## Working-Tree API Verification

An isolated synthetic product group was created against working-tree AppHost on 2026-09-17. One
two-memory payload used fixed caller create UUIDs and one new-to-new link.

| Check | Observed |
|---|---|
| Dry-run | `created=2`, `linked=1`, both fixed UUIDs returned, no blob addresses |
| Write | Same counts and UUIDs as dry-run |
| Read-back | Read-token depth-one traversal returned exactly one path |
| Teardown | Supported `scripts/stop-dev-stack.sh`; persistent data volumes retained; temporary AppHost user secrets removed |

This proves API mechanics and capability use, not delegated model token usage. It therefore does not
close the blocker above.
