# Independent synthetic knowledge evaluation

Expectations were frozen October 3, 2026 before model runs or service tuning. `fixtures.json` SHA256 is `74ffd537a0e72327be3ed77c256ae70e212bf8b9edb6d87087d2117b6bd52a28`; the harness refuses a changed fixture. It imports only the transport client, never service code or prompts. One development case and six distinct held-out retrieval cases cover conditions, customer exceptions, terminology, intent versus implementation, disagreement, legacy uncertainty, historical applicability and absence. Capture cases cover semantic paraphrase, proposals, explicit approved future intent, ordered corrections and repeated handoffs.

The direct baseline executes frozen keyword alternatives against the real core, fetches full cited bodies and synthesizes using an independently fixed prompt. The service receives the question without baseline query terms or expected answers. Both use the same scoped synthetic corpus. Baseline provider usage, service provider usage, main-agent wire context estimates, tool turns and wall time are recorded separately. Main context estimates count UTF-8 bytes / 4; they are not exact tokenizer measurements. Service evidence bodies count when returned, so the harness does not hide a large response. Missing cost or usage is unknown. Human clarification frequency is visible in receipts; manually review meaningful questions separately.

Use Python 3.9+. No provider key or credential may be a command argument. Supply `CONTEXT_MEMORY_BASE_URL`, `CONTEXT_MEMORY_READ_TOKEN`, `CONTEXT_MEMORY_WRITE_TOKEN`, `MIMI_KNOWLEDGE_URL`, `MIMI_KNOWLEDGE_READ_TOKEN`, `MIMI_KNOWLEDGE_WRITE_TOKEN`, `MIMI_EVAL_LLM_URL` (OpenAI-compatible /v1 endpoint), `MIMI_EVAL_LLM_KEY`, `MIMI_EVAL_LLM_MODEL` inside the runtime environment. Alternatively `MIMI_EVAL_CREDENTIALS_FILE` names an external protected KEY=value file parsed inside Python; environment wins. Restrict Windows ACLs; Unix mode 600 is checked. The baseline model should match the service's standard generative model. Provider credentials go only to the baseline HTTP process; no subprocess or tool-using model is launched.

Run from the repository root, with state and reports outside Git:

```text
python scripts/knowledge-evaluation/run_evaluation.py freeze
python scripts/knowledge-evaluation/run_evaluation.py seed --state EXTERNAL_STATE.json
python scripts/knowledge-evaluation/run_evaluation.py run --state EXTERNAL_STATE.json --output EXTERNAL_BASELINE_AND_SERVICE.json --split development
python scripts/knowledge-evaluation/run_evaluation.py run --state EXTERNAL_STATE.json --output EXTERNAL_HELDOUT.json --split heldout
python scripts/knowledge-evaluation/run_evaluation.py capture --state EXTERNAL_STATE.json --output EXTERNAL_CAPTURE.json
```

`seed` mutates only a newly generated synthetic repo/ticket scope through supported core HTTP writes. It refuses to overwrite an existing state file, never cleans up another scope, and never reads private domain content. Use a fresh seed/state for rerunning capture evaluation; otherwise later cases can see past writes. Run retrieval before captures to keep the comparison corpus identical. This script does not create or start a service or deploy anything.

The frozen release comparison is service coverage at least the direct baseline, unsupported statements no more than baseline, reduced main context, all capture safety gates, and independent review. Automated scoring uses lexical detail checks, expected UUID citations and coverage enums. It can produce false negatives for good paraphrases and cannot prove all unsupported statements absent. A report always leaves `qualityGateClosed:false` until a reviewer inspects conditions, boundaries, scope, correction handling, lifecycle, missing evidence and each citation/version. Read the entire report, resolve scoring disagreements explicitly, and record actual model IDs and limitations before any release claim. A failed provider run does not close the gate.

Deterministic checks: `python -m unittest discover -s scripts/knowledge-evaluation -p "test_*.py"`.
