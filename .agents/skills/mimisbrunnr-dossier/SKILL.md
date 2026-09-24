# mimisbrunnr-dossier

Compose a **context dossier** — a read-only, focused, cited document plus its findings — for a slice of
the Mímisbrunnr store. **This skill is read-only (LADR-08 / NFR-06): it has no write capability at
all.** It requests a deterministic **bundle** from the Host API, applies judgement to compose the
**dossier**, and writes only the local artefact to a gitignored path. It never writes to the store and
never calls a write endpoint.

Use this when a practitioner wants *everything the store knows about one slice of work, in one readable
artefact* — re-entering a repository, handing reasoning to a colleague, writing a design document, or
assessing the store's gaps and contradictions.

## The two halves — bundle and dossier

| | Who | Deterministic? |
|---|---|---|
| **Bundle** | The Host API (`POST /api/context/dossier/bundle`) | Yes — byte-identical for an unchanged store (NFR-02) |
| **Dossier** | **This skill** (judgement) | No — it is a projection, not a verdict |

The skill *never re-selects*. It takes the bundle and composes. If the composed bundle differs from the
selection the practitioner approved in the preview, the difference is reported (LADR-14), never absorbed.

## Workflow

```bash
# 1. Fetch the bundle for the anchor set. This is the deterministic bundle, not the preview:
#    the NFR-03 preview (prices the selection without bodies) and the LADR-14 preview-vs-bundle
#    difference check are performed by the agent directly against the Host API —
#    POST /api/context/dossier/preview and POST /api/context/dossier/bundle. Read-only.
python3 -B .agents/skills/mimisbrunnr-dossier/scripts/dossier_composer.py \
  bundle --body '{"repo":"kingstown","widenDepth":3}' | head

# 2. Compose from a saved bundle, apply an optional focus, write the artefact (gitignored).
python3 -B .agents/skills/mimisbrunnr-dossier/scripts/dossier_composer.py \
  compose --bundle bundle.json --focus architecture \
  --out .context/mimisbrunnr-dossier/architecture.md
```

`--focus` is a **single-valued bounded enum** — `requirements`, `architecture`, `specification`,
`implementation`, `review` — or omitted for the unfocused default (LADR-12). An unknown focus fails. A
focus is a lens over one unfocused bundle: it changes ordering, weighting and depth, never membership,
and what it does not surface is listed omitted with reason `outside-focus`. `review` inverts the
document (findings first).

## Invoking the judgement

Composition is judgement (LADR-02). The deterministic skeleton in
`scripts/dossier_composer.py` enforces the rules and delivers the invariants; the **semantic** parts —
which restatements are truly equivalent, whether two claims truly conflict, whether there is a gap —
are the agent's to supply as `judgements` to `compose()`:

```python
from dossier_composer import compose
doc = compose(bundle, focus="requirements", judgements={
    "equivalences": [{"uuids": [...], "meaning": "same rule"}],   # consolidation candidates (LADR-05)
    "findings": [{"category": "contradiction", "basis": ..., "memories": [...],   # semantic findings
                  "classification": "analysis"}],
})
```

The composer then enforces the deterministic gates: applicability + lifecycle must match before a
consolidation, and a scoped exception or proposed-versus-shipped pair is never a contradiction
(LADR-04/05). Every finding is emitted with a basis, a scope (the examined material, never the store),
and its memories by identity + version (LADR-13). Findings are focus-invariant in presence.

## What the composer guarantees

- **Ordering** — topological over `supersedes` / `depends_on` / `implements` only; a provenance cycle
  is reported and still produces the document (LADR-07).
- **Consolidation** — merges only where meaning + applicability + lifecycle match; every origin
  retained and reported as origins, never counted as corroboration (LADR-05).
- **Lifecycle** — current / proposed / superseded / no-longer-true / unknown, with capture recency
  never treated as evidence behaviour shipped (NFR-07).
- **Citation** — every substantive statement carries memory identity + version + capture time;
  composition-authored text is marked **analysis** and states its basis; missing provenance is visible
  (NFR-05).
- **Reconciliation** — present + consolidated + omitted-with-reason == the manifest's selected count,
  closed in the dossier itself (NFR-04).
- **Read-only** — the module exposes no write operation; the only artefact is the dossier at the
  requested path (NFR-06).

## Rules

- **Never write to the store, and never call a write endpoint.** Read-only is structural, not a
  policy you can waive.
- **Never call a model from Application or Host** — judgement lives in this skill and nowhere else.
- **Never resolve a contradiction silently.** Apply a stated authority and show it, or report the
  conflict. Recency is not authority.
- **Never truncate silently.** Everything selected is present, collapsed, or omitted with a reason from
  the bounded set.
- **Never implement focus as a selection filter.** It is a lens applied after selection.
- **Never suppress a finding under a focus.** Reorder, never drop.
- **Never emit a finding without a basis**, and never phrase a slice observation as store-wide.
- **Never let a consolidation discard an origin**, and never present re-captures as corroboration.
- **Never compress away a condition to fit a budget.** Narrow and report; a claim shorn of its
  exception is false.
- **`kind = understanding` is a kind like any other** — same selection, citation, reconciliation,
  focus and confidentiality (LADR-15). Its load/import is a separate capability owned by
  `mimisbrunnr-understanding`, not this skill.
- **`near-miss-tag` is evidence-only** (LADR-10). Use the shared
  `mimisbrunnr-context-memory/scripts/near_miss_tags.py` helper via `near_miss_findings()`. No evidence
  means no finding; no tag-graph or full-dossier completeness claim is made.

## Base URL / token

The bundle is requested from the Host API. The base URL is read from `CONTEXT_MEMORY_BASE_URL`
(default `http://localhost:5141`) and the read token from `CONTEXT_MEMORY_READ_TOKEN`, both by a
script — the token value never appears in a committed file (skill-secret-handling). `--base-url`
overrides for a one-off.

## Scripts

| Script | Purpose |
|---|---|
| `scripts/dossier_composer.py` | Read-only bundle request + dossier composition (ordering, consolidation, lifecycle, citation, findings, focus, reconciliation), and the `near_miss_findings()` helper |

## Test

Committed harness: `python3 -B .agents/skills/mimisbrunnr-dossier/tests/run_tests.py` (34 tests).

## Related

- `docs/hlds/005-contextual-export/` — design (LADRs, NFRs), the determinism boundary, the contract.
- `.agents/skills/mimisbrunnr-context-memory/` — the capture skill (sole **writer**). This dossier
  skill is a reader; do not conflate the two. Reuses its `near_miss_tags.py` helper.
- `.agents/skills/mimisbrunnr-understanding/` — the load/transfer sibling (HLD-007); owns the
  Understanding kind's load/import, which this read-only skill does not.
