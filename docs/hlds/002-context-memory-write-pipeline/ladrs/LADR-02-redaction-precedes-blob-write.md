# LADR-02: Redaction precedes the body write

**Status:** Accepted

## Context

Captured session content may contain API keys, tokens, connection strings and password assignments.

Bodies are content-addressed: an object's address is the hash of its raw content. Two properties
follow, and together they remove every corrective option. The object is **immutable** — it cannot be
edited in place. Its address is **stable** — identical bytes always produce the same address, so the
same secret written again resolves to the same object.

The only remedy after the fact is to drop the reference and write a scrubbed copy, which leaves the
original object present and creates an address that no longer resolves to wanted content. Deleting the
object outright is worse: identical bytes share one address, so deletion can destroy content a
different version still legitimately references.

## Decision

**Place** redaction at stage 2, before any content reaches storage.

Detection is fingerprint-based — matching well-known secret shapes rather than attempting to
understand content. The specific shape list is an implementation concern; the contract is that a
candidate with a detected sensitive span is scrubbed **before** the body write.

This ordering is **not negotiable**. It is the only point at which prevention is still possible.

## Alternatives Considered

- **Redact at read time** — rejected: the secret is already in the immutable object and in the database summary. Strictly worse, because it has been persisted and the read path now carries a permanent cost.
- **Delete the object on detection after the fact** — rejected: identical bytes share one address, so deletion can destroy content another version still references.
- **Encrypt bodies and skip redaction** — rejected: solves storage-at-rest, not the problem. A secret retrieved and re-injected into agent context is exposed regardless of how it was stored.

## Consequences

- A detected secret never becomes an immutable object, which is the only outcome that does not require a remedy.
- Every write pays detection cost, accepted as small relative to the model call the write already makes.
- Detection is fingerprint-based, so a novel secret shape passes. This is a real residual risk, bounded rather than eliminated (NFR-01).
- The write path gains a mandatory stage that cannot be skipped for performance.

## Related

- **LADR-03** — what happens when detection fires.
- **NFR-01** — how containment is verified.
