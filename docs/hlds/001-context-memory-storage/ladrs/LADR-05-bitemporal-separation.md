# LADR-05: Business time and system time are independent axes

**Status:** Accepted

## Context

Two questions look like one until they disagree: *when was this true in the world*, and *when did the
store believe it*.

A fact recorded today may describe something that ceased being true months ago. A record may also be
corrected without the real-world window moving at all. These are different events with different
dates, and both are real.

## Decision

**Model** validity and record history as two independent axes, neither derived from the other.

Business time is a validity window on the versioned row. System time is the version chain plus a
creation timestamp. A query can ask what is true now, and separately what the store held at a past
moment.

Business time is **derived from the source date when known**, not defaulted to the moment of capture —
otherwise the axis is decorative, recording only when someone happened to type it in.

The argument for separation is not elegance. Without it, **correcting a record is indistinguishable
from the world changing**: a typo fix and a genuine change produce identical evidence, and every
staleness judgement downstream becomes unreliable.

### Absolute-time representation (2026-10-13)

Both axes and provenance timestamps use NodaTime `Instant`, not local or offset-bearing times.
An incoming ISO-8601 timestamp with `Z` or an explicit UTC offset is converted to an instant at
serialization; unzoned/date-only values are rejected rather than interpreted using the server's
local timezone. JSON and Markdown emit canonical UTC `Z`. Native Npgsql/EF NodaTime mappings keep
the existing `timestamptz` columns; no data rewrite or schema migration is required.

This closes the write failure where a valid `+02:00` request passed planning but Npgsql rejected
the retained `DateTimeOffset` during persistence. Read paths, snapshots and JSONB provenance share
the same absolute-time model, so normalization is not dependent on a particular API caller.

## Alternatives Considered

- **A single validity axis** — rejected: cannot distinguish correction from change.
- **Deriving business time from the version chain** — rejected: makes the world's timeline a function of our recording habits.
- **Defaulting validity to capture time** — rejected: decorative bitemporality, which is worse than none because it looks trustworthy.

## Consequences

- Three distinct questions become answerable: what is true now, what did we believe then, and did this record change because the world did or because we were wrong.
- Retrieval can exclude stale knowledge without deleting it.
- Every write path must decide the business-time origin rather than accepting a default, which is a small ongoing cost paid at capture.
- Two timestamps invite conflation by anyone reading the schema without this context, which the context files counter explicitly.

## Related

- **LADR-04** — supplies the system-time axis.
