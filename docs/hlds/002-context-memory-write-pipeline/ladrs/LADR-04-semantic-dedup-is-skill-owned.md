# LADR-04: Semantic deduplication belongs to the skill

**Status:** Accepted

## Context

*"PostgreSQL is the storage engine"* and *"we store in Postgres"* are the same subject. So are
*"tags do not version"* and *"classification is unversioned"*.

The database can enforce that an identical subject slug does not recur within a group. It cannot
enforce that two differently-worded subjects are the same, because semantic equivalence is not
expressible in SQL. No index, constraint or trigger closes this gap.

Two independent simulation trials named this the **top write-path risk in the project**. One recorded
its own string-similarity heuristic returning nothing on exactly the Postgres pair above.

## Decision

**Assign** semantic subject matching to the skill, and treat the database's uniqueness index
explicitly as an **exact-match backstop only**.

Matching runs on the **subject**, not the claim, and **across groups** rather than within one —
grouping is episodic, by work item, so the same subject legitimately arises under different tickets. A
within-group check would miss precisely the duplicates that matter.

Recall is narrowed by classification first, then a bounded candidate set is judged. The judgement is
the model's; string similarity may act as a **negative-only** pre-filter and is never the deciding
signal on its own.

The failure is **silent in both directions**, which is what makes it dangerous. A missed match inserts
a near-duplicate that dilutes every future retrieval; a false match rewrites canon under a subject that
never changed. Accuracy is therefore measured as **recall and precision** against authored positive and
negative pairs, not asserted as "deduplication held".

## Alternatives Considered

- **String or trigram similarity as the decision** — rejected: demonstrably fails on the motivating case, and a threshold tuned to catch it produces false matches on related-but-distinct subjects.
- **Within-group matching only** — rejected: grouping is episodic, so the duplicates that matter are cross-group by construction.
- **Vector similarity search** — deferred: index-first retrieval outperforms vectors below roughly a thousand records, and the volume does not yet justify the infrastructure.
- **Match on the claim rather than the subject** — rejected: the claim is the thing that legitimately changes, so matching on it defeats versioning entirely.

## Consequences

- Deduplication quality is a property of the skill, not a guarantee of the schema — the single largest correctness risk in the system, and named as such.
- Cross-group matching costs one traversal, shared with link derivation and ticket uniqueness.
- Text search performs no stemming today, so recall is weaker than ideal; improvement is deferred until measurement justifies it.
- Testing must be adversarial, including negative controls, or it measures nothing.

## Related

- **LADR-01** — the traversal this shares.
- **NFR-02** — how accuracy is measured.
