# LADR-02: Where feedback lives — the open question

**Status:** Draft

## Context

Recall feedback has to be written somewhere, and the obvious answer — a counter on the memory row — is
the one most likely to be wrong.

Retrieval is a read. A counter column turns every read into a write: rows are dirtied, a frequently
recalled memory becomes a contention point, and the read path acquires write-path failure modes. The
design has been explicit that constraints and mechanisms should not be added casually, and this would
add one on the hottest path in the system.

Three placements are viable and none is obviously correct, which is why this HLD is in discovery.

## Decision

**Defer the placement**, and record the three candidates with what each costs. This LADR exists to
prevent the decision being made by default during implementation.

**A — Counter on the memory row.** Simplest to query; "never recalled" is a predicate. But every read
becomes a write, it contends on popular rows, and it discards history — a count cannot distinguish
recalled-once-last-year from recalled-weekly.

**B — Append-only event records.** Reads still write, but appends do not contend and history is
preserved, so recency and frequency both become answerable. Costs a growing record set needing
retention, and "never recalled" becomes an anti-join rather than a predicate.

**C — Derive from telemetry.** Observability now exports traces and metrics. A recall could emit a
span carrying memory identities, and "never recalled" derived by comparing against the store. **No
store writes at all**, which fully satisfies the read-path concern. But telemetry is ephemeral and
capacity-bounded, correlating identities across systems is awkward, and it makes a tuning signal
depend on a debugging tool's retention.

**Initial lean: B**, because it answers recency and frequency, which A cannot, without depending on a
retention policy owned elsewhere, which C does. This is a lean, not a decision.

## Alternatives Considered

Covered above as A, B and C. A fourth — writing feedback to the object store — was dismissed
immediately: content addressing makes objects immutable, which is precisely wrong for an accumulating
mutable signal.

## Consequences

- The choice is visible and must be made deliberately rather than emerging from whichever is easiest to type.
- Whichever is chosen, NFR-02 constrains it: retrieval latency must not change measurably.
- A and B make the store responsible for its own usage data; C makes an observability tool responsible for it.

## Open

- **The placement itself.** Resolved by prototyping against real usage volume, not by argument. The deciding question is whether recency matters — if it does, A is eliminated.
- Whether feedback should survive a restore. It is disposable (LADR-04), so probably not, and that materially simplifies B.

## Related

- **LADR-01** — the decision this implements.
- **NFR-02** — the constraint that bounds every option.
