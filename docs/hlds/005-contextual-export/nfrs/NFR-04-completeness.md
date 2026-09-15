# NFR-04: Completeness

**Status:** Draft

## Requirement

Nothing selected is lost without being reported. For every export, the arithmetic must close:

```
bundle item count == claims present in the dossier
                  + items collapsed into a present claim
                  + items listed as omitted, each with a reason
```

- Every omission carries a reason from a **bounded set** defined before the first export is produced: cap reached, depth reached, unreadable body, collapsed into another claim, hidden by scope, `outside-focus`. Free-text reasons are forbidden — a bounded set is countable, and free text is how unclassifiable omissions accumulate unnoticed.
- Hidden material never enters the selected-item reconciliation. Scope exclusion is a generic
  policy disclosure, not an itemized list or hidden count; visible cap flags must not reveal hidden
  branches (NFR-01, HLD-003 LADR-08).
- **`outside-focus` is what makes a focus auditable** (LADR-12). A lens may set material aside; it may not do so invisibly. Under a narrow focus this list is expected to be long, and that is the requirement working rather than failing.
- **Findings use the same discipline.** The finding taxonomy is bounded and fixed up front: `gap`, `contradiction`, `equivalence-uncertain`, `superseded-still-referenced`, `stale`, `unattributed`, `no-links-in-slice`, `weak-summary`, `provenance-cycle`, `near-miss-tag`. Adding a category later changes the meaning of every earlier export.
- **Two names in that set are deliberate** (LADR-13). `no-links-in-slice` replaces the earlier `orphan`, because a slice cannot establish that a memory is unlinked anywhere in the store. `weak-summary` is classified as **analysis**, not observation.
- Every finding names the memories it concerns, by identity and version, and carries its **basis** and its **scope**, so it is actionable and checkable without re-deriving it (LADR-13).
- **`near-miss-tag` is evidence-only skill output** (LADR-10): supporting memory UUID/version,
  concrete basis, examined scope and observation/analysis classification are mandatory. Use only
  already-authorized examined evidence; inferred synonymy or under-selection is analysis. No extra
  search, scope broadening, hidden IDs/counts or claim about unseen excluded records is permitted.
  No evidence means no finding. This interim contract has an executable offline helper;
  the full dossier NFR remains Draft and tag identity/synonyms remain blocked.
- **Accounting for every selected record is not exhaustive product knowledge** (`BR-30`). The document reports the limits it reached *and* that its findings are not claimed to be complete. `None detected` is always qualified by the examined scope.
- The reconciliation is stated **in the dossier**, not only asserted in a test. A reader must be able to see that the arithmetic closed.

## Verification

**Implemented interim:** `near_miss_tags.py` validates explicit approved UUID/version scope references,
exact supporting quotes and bounded payloads. It emits analysis findings only for caller/skill-relevant
records failing the exact tag predicate, preserves selected references/disclosure and sorts findings.
The skill harness includes fixtures, stdin/stdout execution, bounds and no-extra-I/O checks.
The [dated review revalidation](../../003-graph-edges-on-age/nfrs/NFR-02-ticket-traversal-measurements.md#2026-09-15-review-revalidation)
records the post-schema-fix harness run; the earlier 34-test checkpoint is historical. Run
`python3 .agents/skills/mimisbrunnr-context-memory/tests/run_tests.py` for the current harness,
whose count also includes unrelated skill tests. This does not evaluate LLM relevance judgement or prove the full dossier NFR;
the tests below for full composition remain required, not claimed passed.

- **Skill-level test** — compose a dossier from a fixture bundle; parse the produced document and assert the reconciliation closes exactly. This is the load-bearing test of the whole design: it is the one that catches a composition that quietly dropped material.
- **Skill-level test** — assert every omission reason and every finding category is drawn from the bounded set; an unknown value fails.
- **Skill-level test** — assert every finding carries a basis and a scope qualification, and that no finding phrases a slice observation as a store-wide claim.
- **Skill-level near-miss fixtures**: an evidence-backed vocabulary mismatch cites actual examined
  UUID/versions and labels inferred synonymy as analysis; an unsupported plausible synonym and an
  empty result emit no `near-miss-tag`. Assert zero additional searches/traversals, unchanged selected
  membership, zero writes, no invented or hidden references/counts, and no claim of unseen exclusions.
- **Skill-level test** — a fixture bundle where a body is unreadable: assert the item is reported as omitted with the unreadable reason rather than skipped.
- **L1** — assert the bundle's own manifest count equals the number of items it carries, so the arithmetic starts from a trustworthy left-hand side.
- **Fixture with a provenance cycle** — assert the cycle is reported as a finding and the sort still terminates (LADR-07).
- **Skill-level test, per focus** — compose every focus from one fixture bundle; assert the reconciliation closes for each, that the set of selected memories accounted for is identical across focuses, and that every finding present in the unfocused dossier is present in each focused one.

## Acceptance Criteria

- The reconciliation closes exactly for every dossier, and is visible in the dossier itself.
- Every omission reason and finding category comes from the bounded set; both sets are defined before first use.
- No finding asserts a store-wide or product-wide fact; every one is scoped to the examined material, and `None detected` carries that qualification.
- Every focus of one bundle accounts for the same selected memories, and carries every finding the unfocused dossier carries.
- Every finding names its memories by identity and version.
- Every `near-miss-tag` satisfies the evidence-only fixture checks; no finding broadens search or
  claims tag graph/synonym support. Deterministic helper checks exist; full dossier verification and
  semantic judgement evaluation are not established by this documentation sync.
- An unreadable body is an omission with a reason, never a silent skip.
- A provenance cycle is reported and does not prevent the document being produced.

## Applies To

Goal 2 (a composed document) and Goal 3 (findings as first-class output). LADR-05 (collapse must not
lose), LADR-07 (cycles), LADR-04 (contradiction findings). `BR-23`, `BR-27`, `BR-28`, `BR-29`, `BR-30`.
