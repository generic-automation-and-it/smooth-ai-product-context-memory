# LADR-06: Fuzzy stays rejected; prefix covers typeahead

**Status:** Draft

## Context

HLD-001 NFR-02 measured `pg_trgm` at equal recall to `english` on its fixture (figures
now under a stale-before-quoting banner after the 2026-09-24 predicate change) with an
extension, two extra GIN indexes, and threshold-sensitive semantics — and **rejected** it, with a
reopening threshold: HLD-004 recall-feedback data showing repeated misses on misspelled
queries that stemming cannot serve. Keyword search re-raises the question because
Obsidian tolerates typos. Note: the HLD-004 reopening-evidence note (2026-09-20)
records the threshold MET on two observed misses (`postgress replication`,
`kubernetess`) — the rejection still stands until HLD-001 reruns its fixture with
those cases added and re-measures; this HLD does not pre-empt that rerun.

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
  becomes the evidence that could reopen this decision — the honest loop. Watcher and
  cadence follow the HLD-004 rule (no dashboards/alerting): occasional human review via
  the `mimisbrunnr-muninn-recall-feedback` skill, on demand — a cluster is ≥ 2 observed
  misses on misspelled/surface-variant queries stemming cannot serve, each triggering
  the HLD-001 harness rerun with those cases added.
- No new extension, migration, or index in this HLD.

## Related

- HLD-001 `NFR-02-recall-tuning-measurements.md` — the authority for the rejection.
