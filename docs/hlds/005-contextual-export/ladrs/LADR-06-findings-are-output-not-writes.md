# LADR-06: Findings are output; recording one is an ordinary capture

**Status:** Draft

## Context

An export produces findings that are obviously worth keeping: a contradiction nobody had recorded, a
gap that should become a question, a summary too weak to ever be found again. The store exists to hold
exactly that kind of knowledge, and the export has just derived it.

The cheap move is to write them while they are in hand — add the missing `contradicts` edge, capture the
gap as a question, flag the weak summary. Each write is small, plausible, and a side effect of a read.

`BR-31` forbids it, and `BR-01`'s reasoning is why: capture quality depends on judgement applied at a
checkpoint, and a write that happens as a byproduct of reading has no checkpoint and no judgement. A
composition error would become stored fact, and stored fact is what everything downstream trusts.

## Decision

**Emit** findings as output only. An export performs zero writes.

A finding the practitioner wants to keep is captured afterwards through the normal write path, with its
normal gates: preflight, redaction, dedup, atomicity, and the digest. That path already decides whether
something is a new memory, a version bump or a skip, and already gates the kinds that need approval.
A finding entering through it is subject to all of that; a finding written as a side effect is subject
to none of it.

This applies to the graph as much as the tables. Adding the missing `contradicts` edge is attractive
because it looks like recording an observation rather than making a claim. It is a claim: it asserts
that two memories disagree, on the authority of one composition pass.

## Alternatives Considered

- **Write findings automatically as `proposed` and let the practitioner promote them** — rejected: `proposed` is a gate on *approval*, not on *judgement*. Bulk-writing every finding fills the store with unreviewed rows whose only reviewer is the person the volume would overwhelm.
- **Write only the mechanical findings — the missing edges — and report the rest** — rejected: the missing edge is the least mechanical finding of all. It rests on the composition having correctly judged two claims to conflict (LADR-04).
- **Cache the export so findings are not re-derived on the next run** — rejected: a cache of derived judgements is a second store with no supersession model, and it would drift from the memories it describes.
- **Record that an export ran, without recording its findings** — deferred, not rejected: that is recall-feedback territory, and coupling to it now would bind this design to an HLD still in discovery. See Open.

## Consequences

- An export is unambiguously a read, which makes NFR-06 a hard binary assertion rather than a judgement call.
- Findings are re-derived on every export. Accepted: composition dominates the cost anyway, and a re-derived finding reflects the store as it is now rather than as it was.
- A finding acted on becomes ordinary knowledge with ordinary provenance, indistinguishable from any other capture — which is correct, because that is what it is.
- Nothing accumulates a record of which findings were already reviewed and dismissed, so the same non-issue can be reported repeatedly. This is a real annoyance and is the strongest argument for the deferred alternative above.

## Open

- **Should an export be recorded as an event at all?** HLD-004 is designing recall feedback and an export is a very large recall. Whether an export contributes to that signal — and whether "findings already dismissed" belongs there — must be decided with HLD-004, not ahead of it. Trigger: HLD-004 leaving discovery.

## Related

- **LADR-04** — the contradiction finding this most obviously applies to.
- **NFR-06** — the zero-write assertion.
- **HLD-004** — recall feedback, which owns the question in Open.
