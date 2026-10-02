# LADR-08: Recall evidence stays self-authored; conversational benchmarks are the wrong population

**Status:** Accepted — 2026-10-02

## Context

Every recall figure in this design comes from a fixture its own authors wrote: the text-search tuning
measurement (NFR-02, 14 memories / 11 cases) and the semantic-dedup fixture (HLD-002 NFR-02, 14
scenarios). Self-authored evidence carries an author's blind spots, and this design has already paid
for one — the dedup scorer reported **accuracy under the name recall**, and the cross-group evidence had
to be withdrawn. An independently authored fixture would be one check the authors cannot write around.

MemOS reports LoCoMo, LongMemEval and OmniMemEval scores. The question this record answers is whether
any available external set measures a population **this** system actually claims — and if not, what the
in-use signal covers instead.

## Decision

**Reject.** No available external benchmark measures this system's population, so recall evidence stays
self-authored and is complemented by the in-use recall-feedback signal (HLD-004). Concretely:

- This store holds **curated, distilled, labelled facts** — each an atomic claim with a subject, a
  version chain, facets/tags and a scope. Recall is by **label / ticket / facet / free-text lexemes**,
  and semantic equivalence is judged by the write-path skill.
- **LoCoMo** and **LongMemEval** measure recall over **conversation history**: long multi-session
  dialogues, questions answered from remembered turns. The unit is a dialogue turn, not a distilled
  claim; the retrieval signal is conversational relevance, not a label/facet/lexeme predicate; and the
  metric (answer correctness over dialogue QA) rewards recalling **episodes**, not retrieving **canon**.
- **OmniMemEval** is the same shape over agent interaction traces.

A score on any of them would measure a population this system does not store, retrieved by a mechanism
it does not use, scored by a metric its NFRs do not state. A perfect score there is the
"wrong-metric" defect HLD-002 NFR-02 already records, at a larger scale — **worse than no score**, because it
reads as validation.

## Alternatives Considered

- **Adopt LoCoMo / LongMemEval / OmniMemEval** — rejected: wrong population (dialogue turns, not
  distilled labelled claims), wrong retrieval signal (conversational relevance, not
  label/facet/lexeme), wrong metric (answer generation, not retrieval of the claim). The mismatch is in
  the *population*, so no re-weighting of the metric fixes it.
- **Adopt an external *authoring process* instead of a dataset** (an independent practitioner authors
  pairs from a described system, never seeing the implementation) — deferred, not rejected on the
  merits. It would address the blind-spot concern directly, but it is a process this repository would
  still have to run and maintain, and the in-use signal below already supplies independence of a
  stronger kind: real queries against the real store. Reopen if the self-authored fixtures are ever
  shown to miss a failure class the in-use signal cannot see.
- **Adopt a general retrieval benchmark (BEIR-style)** — rejected: those measure document retrieval over
  a corpus with a relevance-judged query set. Closer in shape, but the corpus is prose documents, not
  versioned labelled claims with scope and status, and relevance is topical, not "is this the same
  subject". Still the wrong population.

## Consequences

- **No external dataset is adopted.** The self-authored fixtures stay, with their denominators stated at
  their source (HLD-001 NFR-02: 14 memories / 11 cases; HLD-002 NFR-02: fourteen scenarios) and the
  dedup fixture's axes labelled (`recall_positive` / `precision_negative` / `not_dedup`), which is what
  makes them auditable even though they are not independent.
- **The in-use signal covers what an external fixture cannot.** HLD-004's never-recalled set and
  miss-rate measure *this* store's findability over time, on real queries against the real corpus — the
  evidence for the deferred recall-quality decisions (e.g. `pg_trgm`'s reopening threshold, recorded in
  [HLD-001 NFR-02](../nfrs/NFR-02-recall-tuning-measurements.md)). That is a different and stronger
  independence than a third-party fixture: the queries are the practitioner's, not an author's.
- **Reopening threshold.** Reconsider an external set only if it (a) stores distilled labelled claims
  with scope/status, (b) retrieves by label/facet/lexeme, and (c) scores retrieval of the claim rather
  than answer generation. None of LoCoMo / LongMemEval / OmniMemEval is that, and this record exists so
  the proposal is answered by the criteria rather than re-litigated.

## Related

- **NFR-02 recall-tuning measurements** — the other self-authored fixture, whose denominators are stated
  (14 memories / 11 cases); it is not the wrong-metric scorer, which is HLD-002's below.
- **HLD-002 NFR-02** — the semantic-dedup fixture whose scorer reported accuracy under the name recall;
  its denominator correction is why independence was asked for.
- **HLD-004** — the in-use recall-feedback signal (never-recalled, miss-rate), the complement adopted
  here.
- **BRD-001** — recall quality is a findability concern, not a conversational-memory one.
