# LADR-06: Fuzzy stays rejected; prefix covers typeahead

**Status:** Draft

## Context

HLD-001 NFR-02 measured `pg_trgm` at equal recall to `english` on its fixture (figures
now under a stale-before-quoting banner after the 2026-09-24 predicate change) with an
extension, two extra GIN indexes, and threshold-sensitive semantics — and **rejected** it, with a
reopening threshold: HLD-004 recall-feedback data showing repeated misses on misspelled
queries that stemming cannot serve. Keyword search re-raises the question because
Obsidian tolerates typos. Nothing in the miss data has yet met that threshold.

## Decision

**Keep** the rejection. Typeahead and prefix completion are served by `:*` prefix
(LADR-02) on the existing GIN index — no extension, no threshold. True fuzzy
(`pg_trgm` `%>`, similarity thresholds, misspelling tolerance) is **not** part of this
design; its reopening condition stays exactly the recorded one (observed miss cluster +
harness rerun with those cases added).

## Alternatives Considered

- **Adopt `pg_trgm` now for typo tolerance** — rejected: pays extension + storage +
  threshold semantics for no measured recall gain on the current fixture.
- **Client-side edit-distance rerank** — rejected: filters materialised rows after the
  database, defeating the every-predicate-DB-side rule.

## Consequences

- Misspelled queries still miss; the miss is recorded by recall-feedback (HLD-04) and
  becomes the evidence that could reopen this decision — the honest loop.
- No new extension, migration, or index in this HLD.

## Related

- HLD-001 `NFR-02-recall-tuning-measurements.md` — the authority for the rejection.
