# LADR-01: Record recall outcomes, including misses

**Status:** Accepted — 2026-09-18

## Context

Capture is fully instrumented — every write returns a digest naming what was created, versioned,
linked and skipped. Retrieval reports nothing at all. The store therefore knows precisely what it was
told and nothing about whether any of it was ever wanted.

Stemming and trigram similarity for candidate recall were deferred *until measurement justifies
them* (HLD-001 AGENTS.md:63; HLD-002 AGENTS.md:68). The system is entering daily use, which is the
intended source of that measurement — but daily use without instrumentation produces recollection,
not evidence.

## Decision

**Record** an outcome for every retrieval: which memories were returned, and — equally — that a
retrieval returned nothing.

Both halves matter and the second is the one usually omitted. A retrieval that returns nothing is the
signal that the store failed, either by not holding the knowledge or by holding it and not surfacing
it. Recording only successful returns produces a record of what recall already does well and silence
about everything it does badly.

Scope is deliberately narrow: this records outcomes for later examination. It does not rank, score,
or feed anything back into retrieval.

## Alternatives Considered

- **Record nothing; rely on the operator noticing** — rejected: the deferred decisions need comparable evidence, and impressions of recall quality are unreliable precisely because a miss is unmemorable.
- **Record only successful recalls** — rejected: omits the failure signal, which is the more actionable half.
- **Infer usage from application logs** — rejected: logs are formatted for humans and retained by an unrelated policy; deriving a stable signal from them is fragile.

## Consequences

- Capture quality becomes assessable without human review — "never recalled" is a cheap proxy for a judgement that is otherwise expensive to make.
- The deferred recall-quality decisions gain a basis, so they can be settled or re-deferred with a reason.
- Retrieval gains a responsibility it did not have, with cost and confidentiality implications addressed in LADR-03 and NFR-02.
- A new record type accumulates, which must be disposable (LADR-04) or it becomes a second store to reason about.

## Open

- ~~Whether a "miss" is recorded for every empty result or only where the caller signals the answer was
  expected.~~ **Resolved — 2026-09-18: a miss is recorded for every empty result.** The caller does not
  currently signal expectation, so the second option would add a contract the handler cannot honour and
  would silently drop the miss signal in the common case. The noise of recording every empty result is
  bounded and classifiable, and NFR-03's miss-rate question is well-defined over all empty retrievals.
  The emission point is `QueryMemories.Handler`, after the response is materialised, off the critical path.

## Related

- **LADR-02** — where the record lives.
- **LADR-03** — what it may contain.
