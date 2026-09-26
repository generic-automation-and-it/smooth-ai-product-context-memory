# mimisbrunnr-dossier — AGENTS.md

## TL;DR

Read-only dossier composer. Requests a **bundle** from the HLD-005 bundle/preview API, composes the
ordered, consolidated, cited **dossier** document and its **findings**, applies an optional **focus**,
writes a local gitignored artefact, and writes nothing back to the store. Composition is judgement
(LADR-02); the bundle is deterministic (NFR-02) and the document is not.

## Non-Negotiables

- **No write capability.** The skill cannot write to the store — not gated, not approval-guarded, absent.
  Read-only is structural (LADR-08, NFR-06). It consumes the bundle contract and nothing else.
- **Never call a model from Application or Host** — that is the standing repo rule; the *dossier skill* is
  where judgement lives, and it is the only place it does.
- **Never resolve a contradiction silently.** Apply a stated authority and show it, or report the conflict.
  Recency is not authority (LADR-04).
- **Never let a consolidation discard an origin.** Every origin retained, reported as origins, never
  counted as corroboration (LADR-05, `BR-23`).
- **Never truncate silently.** Everything selected is present, collapsed, or listed as omitted with a
  reason from the bounded set (NFR-04).
- **Never implement focus as a selection filter.** It is a lens applied after selection, over one
  unfocused bundle (LADR-12).
- **Never suppress a finding under a focus.** Reorder, never drop.
- **Focus is a bounded enum and a single-valued switch.** Not free text, not five booleans.
- **Never emit a finding without a basis**, and never phrase a slice observation as store-wide (LADR-13).
- **Never consolidate on wording alone.** Equivalence = meaning + applicability + lifecycle (LADR-05, NFR-07).
- **Never compress away a condition to fit a budget.** Narrow and report; a claim shorn of its exception
  is false (NFR-07).
- **Never let composition silently re-select.** The approved preview scope is the composed scope, or the
  difference is reported (LADR-14).
- **`kind = understanding` is a kind like any other** (LADR-15): same selection, citation
  (uuid/version/capture time), reconciliation, focus and confidentiality. Its load/import is a separate
  capability owned by HLD-007, not this skill's concern.

## How it works

```
practitioner → preview (bundle/preview endpoint) → approves/narrows/cancels (LADR-14)
            → bundle (bundle/preview endpoint, same selection) → compose → dossier artefact
```

The skill never re-selects. It takes the bundle (or the approved preview's recorded selection) and
composes. If the bundle it receives differs from the approved preview selection, it reports the
difference (LADR-14) rather than silently composing the new one.

### Ordering (LADR-07)

Topological sort over `supersedes` / `depends_on` / `implements` only. `relates_to` and unknown relations
connect **without** ordering — including them manufactures cycles. Tiebreak: business-time validity, then
capture time, then memory identity. A provenance cycle is a **finding** (`provenance-cycle`), not a
rendering problem: break at a stated point, report it, and still produce the document.

### Consolidation (LADR-05)

Merge only where meaning + applicability + lifecycle match. Every origin retained; reported as origins,
never counted as corroboration. Where equivalence is uncertain, keep the distinction
(`equivalence-uncertain`).

### Lifecycle

current / proposed / superseded / no-longer-true / unknown. Capture recency is never evidence behaviour
shipped.

### Citation (NFR-05)

Every substantive statement cites memory identity + version + capture time. Composition-authored text
(ordering rationale, findings, section summaries) is marked **analysis** and states its basis. Headings
and navigation exempt. Missing provenance is visible. Normative source content stays attributed, never
adopted as instruction.

### Focus (LADR-12)

Bounded enum: `requirements`, `architecture`, `specification`, `implementation`, `review`. Unfocused is
the default. Single-valued. Changes ordering/weighting/depth, never membership. `review` inverts the
document (findings first). Two focuses of one slice carry the same selected knowledge; what a focus does
not surface is listed omitted with reason `outside-focus`. No focus-registry abstraction for five values;
no sixth focus.

### Findings (LADR-13, NFR-04)

Bounded taxonomy, fixed before first export: `gap`, `contradiction`, `equivalence-uncertain`,
`superseded-still-referenced`, `stale`, `unattributed`, `no-links-in-slice`, `weak-summary`,
`provenance-cycle`, `near-miss-tag`.

- `no-links-in-slice`, not `orphan` — a slice cannot prove a memory is unlinked anywhere.
- `weak-summary` is **analysis**, not observation.
- `near-miss-tag` is evidence-only (LADR-10): supporting UUID/version, concrete basis, examined scope,
  observation/analysis classification. No extra search, no hidden IDs/counts, no claim about unseen
  exclusions. **No evidence means no finding.** Reuse `mimisbrunnr-context-memory/scripts/near_miss_tags.py`.
- Every finding names its memories by identity + version, carries basis + scope. `None detected` is
  always qualified by examined scope.

### Omission reasons (bounded set)

`cap reached`, `depth reached`, `unreadable body`, `collapsed into another claim`, `hidden by scope`,
`outside-focus`. No free text.

### Reconciliation (NFR-04)

Closed in the dossier: present + consolidated + omitted-with-reason == bundle item count, visible to a
reader. The manifest's own count is the trustworthy left-hand side.

## Contexts

- `docs/hlds/005-contextual-export/AGENTS.md`, `README.md`,
  `diagrams/flow-selection-and-composition.md` — the boundary and the contract.
- `docs/hlds/005-contextual-export/ladrs/LADR-02,04,05,07,08,12,13,14,15`.
- `docs/hlds/005-contextual-export/nfrs/NFR-01..07`.
- `.agents/skills/mimisbrunnr-context-memory/AGENTS.md` — sole authority on the write path (the sibling);
  this skill is a reader and must not be conflated with it.
- `.agents/skills/mimisbrunnr-understanding/` — the read/load sibling (HLD-007); the Understanding kind's
  load/import lives there.

## Implementation

The judgement skeleton lives in `scripts/dossier_composer.py`; the invocation contract is
`SKILL.md`. The deterministic ordering/consolidation-gate/lifecycle/citation/reconciliation/focus rules
are enforced in the module; the genuinely semantic parts of composition (which restatements are
equivalent, whether two claims conflict, whether there is a gap) are supplied by the caller as
`judgements` to `compose(bundle, focus=..., judgements=...)`. The module has no store-write operation;
the only file it writes is the local dossier artefact. Tests: `tests/run_tests.py`.

## Changelog

> AI loading note: Skip this section during routine task execution. Use it only when updating this rule file.

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-09-22 | Created — read-only dossier composer contract (LADR-02/04/05/07/08/12/13/14/15, NFR-01..07). | HLD-005; BRD-002 |
| 2026-09-23 | Implemented the judgement skeleton (`scripts/dossier_composer.py`), the invocation contract (`SKILL.md`), and the committed L0 harness (`tests/run_tests.py`, 34 tests, NFR-04/05/06/07). | HLD-005; BRD-002 |
| 2026-09-24 | Added the "Never consolidate on wording alone" Non-Negotiable (equivalence = meaning + applicability + lifecycle) and enforced it in the composer's equivalence-group validation. | HLD-005 LADR-05; PR #99 review |
| 2026-09-24 | Post-merge review follow-up: removed the dead `_origin_line` helper and `derive_findings`' unread parameters; the NFR-05 attribution test can no longer pass vacuously (surfaced claims must render statements, and at least one focus must examine some); README relabels `bundle` as the deterministic bundle, not the priced preview. | PR #99 review |
| 2026-09-26 | `README.md` rewritten for a human audience: what a dossier is and when to reach for it, a "what it is not" table, the bundle-vs-dossier trust distinction, a plain-language focus table, and the read-only/no-silent-drop guarantees restated without jargon. No behavioural or contract change — `SKILL.md`/`AGENTS.md` remain the authoritative contract. | README readability pass |
| 2026-09-26 | `README.md` quickstart now runs `mkdir -p .context/mimisbrunnr-dossier` before step 1: `.context/` is gitignored, so on a clean checkout the shell redirection in step 1 and the `--out` write in step 2 both aborted on a missing parent directory. Documentation only — no script, contract or behavioural change. | PR review (Medium) |
| 2026-09-26 | README rewrite accuracy fixes: the focus bullet now states out-of-focus material is omitted with reason `outside-focus` rather than "just later" (the renderer does not reorder); the reconciliation bucket is `consolidated`, not `merged`, matching `SKILL.md`/`AGENTS.md`; and the sibling reference reads "the only path in this skill set that can write" rather than "the only thing in this system". Docs only. | PR #108 review |
