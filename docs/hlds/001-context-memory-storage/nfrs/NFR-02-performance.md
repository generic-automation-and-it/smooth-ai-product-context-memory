# NFR-02: Performance

**Status:** Accepted

## Requirement

The compound retrieval query — currently-valid memories filtered by facets, tags, ticket, repository,
initiative, scope and kind, with optional full text — is **served entirely by indexes**.

- **No sequential scan** over any memory or version table at expected volume.
- **Every predicate is executed by the database.** Materialising rows and filtering in application code is a failure of this NFR regardless of wall-clock time, because it defeats the indexes and pulls whole version chains across the wire on a current-only query.
- Retrieval returns **cheap fields only**; bodies are fetched on explicit drill-down.

## Verification

- Capture the query plan for the compound retrieval shape at representative volume; assert index or bitmap access paths, and assert no sequential scan on the memory or version tables.
- Assert the generated SQL contains every filter predicate — a predicate absent from the SQL is being applied in application code.
- Assert a current-only query does not materialise non-current versions.

Wall-clock targets are deliberately **not** specified. Volume is hundreds to low thousands of records
and latency is uncritical; a time-based target would pass trivially and prove nothing. The access path
is the real property.

## Acceptance Criteria

- Plans show no sequential scan on memory or version tables for the compound shape.
- Every filter dimension appears in the generated SQL.
- A current-only query returns only current versions from the database, not filtered afterwards.

## Applies To

Goal 3; LADR-01, LADR-02.
