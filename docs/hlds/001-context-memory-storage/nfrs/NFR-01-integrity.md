# NFR-01: Integrity

**Status:** Accepted

## Requirement

Every guarantee in LADR-07 is provably in the tier it claims. Specifically:

- **Tier 1** — a second current version for one memory is **rejected by the database**, not by the application. Version chain integrity and identity uniqueness likewise.
- **Tier 2** — update or delete against either history table **raises**; the entity set is **exactly seven types**; tags and facets survive a version bump **unchanged, unduplicated, unversioned**; a facet absent from the registry is **accepted**.
- **Tier 3** — subject and ticket uniqueness are documented as soft, and the exact-match backstop rejects an identical subject within one group.

## Verification

Component tests against a real database instance, asserting behaviour rather than restating rules:

- Insert two current versions for one memory; assert the database rejects the second and attributes it to the filtered index.
- Insert v1, bump to v2; assert exactly one current and that v1 remains readable.
- Attempt update and delete on each history table; assert both raise.
- Assert the model exposes exactly the seven expected entity types, as a literal list rather than a count.
- Add tags, bump the version, re-read; assert identical tags and no duplication.
- Write a facet not present in the registry; assert it is accepted.
- Insert the same subject slug twice within one group; assert rejection. Insert it in two different groups; assert both succeed.
- Exercise the cascade-delete bypass and assert it is transaction-scoped — after the transaction ends, a subsequent delete attempt on the same connection raises.

The last case matters disproportionately: a session-scoped bypass would leak across a pooled
connection and silently grant a later unrelated caller permission to delete history.

## Acceptance Criteria

- All cases pass in the standing suite and run on every build, so a regression breaks the build rather than accumulating.
- Each test asserts an observable outcome; none re-derives the rule it is testing.
- A tier-2 mechanism that fails is fixed at the cause. Relaxing the assertion to make an unrelated change pass is a defect.

## Applies To

Goals 1 and 2; LADR-03, LADR-04, LADR-07.
